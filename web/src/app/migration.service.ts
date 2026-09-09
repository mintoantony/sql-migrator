import { Injectable, InjectionToken, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import {
  ColumnMapping, ConnectionRequest, ConnectionTestResponse, GenerateScriptResponse, Issue,
  Mapping, SaveMappingResponse, SessionStatus, TableMapping, ValidateExpressionResponse,
} from './models';

/** How the service opens a server-sent-events connection; replaced with a fake in tests. */
export const EVENT_SOURCE = new InjectionToken<(url: string) => EventSource>('EVENT_SOURCE', {
  providedIn: 'root',
  factory: () => (url: string) => new EventSource(url),
});

@Injectable({ providedIn: 'root' })
export class MigrationService {
  private readonly http = inject(HttpClient);
  private readonly openEvents = inject(EVENT_SOURCE);

  readonly sessionId = signal<string | null>(null);
  readonly status = signal<SessionStatus | null>(null);
  readonly mapping = signal<Mapping | null>(null);
  readonly issues = signal<Issue[]>([]);

  /** Ai:ConfidenceThreshold as configured server-side; 0.75 only until the first status arrives. */
  readonly confidenceThreshold = signal<number>(0.75);

  /** Manual accept/reject overrides from the review grid, keyed "table.column". */
  private readonly manualAccepts = signal<Record<string, boolean>>({});

  readonly blockingCount = computed(() => this.issues().filter(i => i.severity === 'Blocking').length);
  readonly warningCount = computed(() => this.issues().filter(i => i.severity === 'Warning').length);
  readonly hasBlocking = computed(() => this.blockingCount() > 0);

  /** High-confidence proposals arrive accepted; anything below needs a deliberate click. */
  accepted(targetTable: string, targetColumn: string): boolean {
    const key = `${targetTable}.${targetColumn}`;
    const manual = this.manualAccepts()[key];
    if (manual !== undefined) return manual;

    const column = this.column(targetTable, targetColumn);
    return (column?.confidence ?? 0) >= this.confidenceThreshold();
  }

  toggleAccepted(targetTable: string, targetColumn: string) {
    const key = `${targetTable}.${targetColumn}`;
    const current = this.accepted(targetTable, targetColumn);
    this.manualAccepts.update(a => ({ ...a, [key]: !current }));
  }

  private column(targetTable: string, targetColumn: string): ColumnMapping | undefined {
    return this.mapping()?.tables
      .find(t => t.targetTable === targetTable)?.columns
      .find(c => c.targetColumn === targetColumn);
  }

  /**
   * The mapping actually sent to the server: an unaccepted column is dropped from its table and
   * reported as unmapped, the same way a column the model never proposed anything for is
   * reported. That means a rejected mapping onto a NOT NULL target column with no default
   * surfaces as the existing NUL001 blocking issue, rather than the rejection silently doing
   * nothing and the proposal shipping anyway.
   */
  private mappingToSubmit(): Mapping | null {
    const mapping = this.mapping();
    if (!mapping) return null;

    return { ...mapping, tables: mapping.tables.map(t => this.tableToSubmit(t)) };
  }

  private tableToSubmit(table: TableMapping): TableMapping {
    const accepted = table.columns.filter(c => this.accepted(table.targetTable, c.targetColumn));
    const rejected = table.columns.filter(c => !this.accepted(table.targetTable, c.targetColumn));
    if (rejected.length === 0) return table;

    return {
      ...table,
      columns: accepted,
      unmapped: [
        ...table.unmapped,
        ...rejected.map(c => ({ targetColumn: c.targetColumn, reason: 'Rejected in review.' })),
      ],
    };
  }

  testConnection(request: ConnectionRequest): Promise<ConnectionTestResponse> {
    return firstValueFrom(this.http.post<ConnectionTestResponse>('/api/connections/test', request));
  }

  async analyse(source: ConnectionRequest, target: ConnectionRequest, sourceReference: string): Promise<void> {
    const started = await firstValueFrom(
      this.http.post<{ sessionId: string }>('/api/analyse', { source, target, sourceReference }),
    );
    this.sessionId.set(started.sessionId);
  }

  /**
   * Follows the analysis over one server-sent-events request until it finishes. This replaced a
   * half-second poll of GET /api/analyse/{id}: a one-minute run put over a hundred requests in
   * the network tab, which reads as a hundred calls to the model when the model sees two or
   * three. Now the browser opens exactly one request per analysis and the server pushes each
   * step and the final state down it.
   */
  watchUntilDone(): Promise<SessionStatus> {
    const id = this.sessionId();
    if (!id) return Promise.reject(new Error('No analysis has been started.'));

    return new Promise<SessionStatus>((resolve, reject) => {
      const source = this.openEvents(`/api/analyse/${id}/events`);

      source.onmessage = event => {
        const status = JSON.parse(event.data as string) as SessionStatus;
        this.status.set(status);
        this.confidenceThreshold.set(status.confidenceThreshold);
        if (status.state === 'running') return;

        // Close before the server does: an EventSource treats a server-side close as a dropped
        // connection and reconnects, which would start a second stream for a finished run.
        source.close();
        this.mapping.set(status.mapping);
        this.issues.set(status.issues);
        resolve(status);
      };

      source.onerror = () => {
        source.close();
        reject(new Error('Lost the connection to the analysis. Is the API still running?'));
      };
    });
  }

  validateExpression(
    sourceTable: string, targetTable: string, targetColumn: string, expression: string,
  ): Promise<ValidateExpressionResponse> {
    return firstValueFrom(this.http.post<ValidateExpressionResponse>('/api/expression/validate', {
      sessionId: this.sessionId(), sourceTable, targetTable, targetColumn, expression,
    }));
  }

  saveMapping(): Promise<SaveMappingResponse> {
    return firstValueFrom(this.http.post<SaveMappingResponse>('/api/mapping/save', {
      sessionId: this.sessionId(), mapping: this.mappingToSubmit(),
    }));
  }

  generateScript(): Promise<GenerateScriptResponse> {
    return firstValueFrom(this.http.post<GenerateScriptResponse>('/api/script/generate', {
      sessionId: this.sessionId(), mapping: this.mappingToSubmit(),
    }));
  }
}
