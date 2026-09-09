import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import {
  ConnectionRequest, ConnectionTestResponse, GenerateScriptResponse, Issue,
  Mapping, SaveMappingResponse, SessionStatus, ValidateExpressionResponse,
} from './models';

@Injectable({ providedIn: 'root' })
export class MigrationService {
  private readonly http = inject(HttpClient);

  readonly sessionId = signal<string | null>(null);
  readonly status = signal<SessionStatus | null>(null);
  readonly mapping = signal<Mapping | null>(null);
  readonly issues = signal<Issue[]>([]);

  readonly blockingCount = computed(() => this.issues().filter(i => i.severity === 'Blocking').length);
  readonly warningCount = computed(() => this.issues().filter(i => i.severity === 'Warning').length);
  readonly hasBlocking = computed(() => this.blockingCount() > 0);

  testConnection(request: ConnectionRequest): Promise<ConnectionTestResponse> {
    return firstValueFrom(this.http.post<ConnectionTestResponse>('/api/connections/test', request));
  }

  async analyse(source: ConnectionRequest, target: ConnectionRequest, sourceReference: string): Promise<void> {
    const started = await firstValueFrom(
      this.http.post<{ sessionId: string }>('/api/analyse', { source, target, sourceReference }),
    );
    this.sessionId.set(started.sessionId);
  }

  /** Polls until the analysis finishes. The API holds one session, so there is nothing to cancel. */
  async pollUntilDone(intervalMs = 500): Promise<SessionStatus> {
    const id = this.sessionId();
    if (!id) throw new Error('No analysis has been started.');

    for (;;) {
      const status = await firstValueFrom(this.http.get<SessionStatus>(`/api/analyse/${id}`));
      this.status.set(status);

      if (status.state !== 'running') {
        this.mapping.set(status.mapping);
        this.issues.set(status.issues);
        return status;
      }
      await new Promise(resolve => setTimeout(resolve, intervalMs));
    }
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
      sessionId: this.sessionId(), mapping: this.mapping(),
    }));
  }

  generateScript(): Promise<GenerateScriptResponse> {
    return firstValueFrom(this.http.post<GenerateScriptResponse>('/api/script/generate', {
      sessionId: this.sessionId(), mapping: this.mapping(),
    }));
  }
}
