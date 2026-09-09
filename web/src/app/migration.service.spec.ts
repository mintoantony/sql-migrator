import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { EVENT_SOURCE, MigrationService } from './migration.service';
import { Mapping, SessionStatus } from './models';

function runningStatus(step: string): SessionStatus {
  return {
    state: 'running', step, mapping: null, issues: [], failures: [], unmatchedSourceTables: [],
    error: null, confidenceThreshold: 0.75,
  };
}

// Both columns start at or above the 0.75 default threshold, so both arrive pre-accepted —
// isolating the accept/reject tests below to the effect of an explicit toggle rather than
// also exercising the separate "starts unaccepted below threshold" behaviour.
const reviewMapping: Mapping = {
  name: 'Demo', model: 'test', sourceDatabase: 'S', sourceReference: '[S]', targetDatabase: 'T',
  tables: [{
    sourceTable: 'dbo.Customer', targetTable: 'dbo.Client', origin: 'ai', confidence: 0.96, reason: 'Customers.',
    columns: [
      { targetColumn: 'ClientId', rule: 'copy', expression: 'CustomerId', origin: 'ai', confidence: 0.99, reason: 'Key.' },
      { targetColumn: 'FullName', rule: 'concat', expression: "CONCAT(FirstName, ' ', LastName)", origin: 'ai', confidence: 0.9, reason: 'Two parts.' },
    ],
    unmapped: [{ targetColumn: 'Notes', reason: 'No source column.' }],
  }],
};

/** A scriptable stand-in for the browser's EventSource: tests push events and see whether it was closed. */
class FakeEventSource {
  onmessage: ((event: MessageEvent) => void) | null = null;
  onerror: ((event: Event) => void) | null = null;
  closed = false;

  constructor(readonly url: string) {}

  close() { this.closed = true; }

  push(status: SessionStatus) { this.onmessage?.({ data: JSON.stringify(status) } as MessageEvent); }
  drop() { this.onerror?.(new Event('error')); }
}

describe('MigrationService', () => {
  let service: MigrationService;
  let http: HttpTestingController;
  let opened: FakeEventSource[];

  beforeEach(() => {
    opened = [];
    TestBed.configureTestingModule({
      providers: [
        MigrationService, provideHttpClient(), provideHttpClientTesting(),
        { provide: EVENT_SOURCE, useValue: (url: string) => {
          const source = new FakeEventSource(url);
          opened.push(source);
          return source as unknown as EventSource;
        } },
      ],
    });
    service = TestBed.inject(MigrationService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    vi.useRealTimers();
  });

  it('posts a connection test and returns the result', async () => {
    const promise = service.testConnection({ server: 'localhost', database: 'Demo' });

    const request = http.expectOne('/api/connections/test');
    expect(request.request.method).toBe('POST');
    request.flush({ ok: true, version: 'SQL Server 2025', tableCount: 3, error: null });

    const result = await promise;
    expect(result.ok).toBe(true);
    expect(result.tableCount).toBe(3);
  });

  it('stores the session id when analysis starts', async () => {
    const promise = service.analyse(
      { server: 'l', database: 's' },
      { server: 'l', database: 't' },
      '[s]',
    );

    http.expectOne('/api/analyse').flush({ sessionId: 'abc123' });
    await promise;

    expect(service.sessionId()).toBe('abc123');
  });

  it('records blocking issues so the UI can disable generate', () => {
    service.issues.set([
      { code: 'EXP002', severity: 'Blocking', message: 'nope', table: 'dbo.Client', column: 'FullName' },
      { code: 'TYP002', severity: 'Warning', message: 'narrow', table: 'dbo.Client', column: 'Summary' },
    ]);

    expect(service.hasBlocking()).toBe(true);
    expect(service.blockingCount()).toBe(1);
  });

  describe('watchUntilDone', () => {
    it('opens one event stream, tracks progress, then resolves and stores the mapping once ready', async () => {
      service.sessionId.set('sess-1');
      const mapping: Mapping = {
        name: 'Demo', model: 'test', sourceDatabase: 'S', sourceReference: '[S]', targetDatabase: 'T', tables: [],
      };

      const promise = service.watchUntilDone();

      expect(opened.map(s => s.url)).toEqual(['/api/analyse/sess-1/events']);
      opened[0].push(runningStatus('Matching tables'));
      expect(service.status()?.step).toBe('Matching tables');
      opened[0].push(runningStatus('Validating expressions'));
      expect(service.status()?.step).toBe('Validating expressions');
      expect(opened[0].closed).toBe(false);

      opened[0].push({
        state: 'ready', step: null, mapping, issues: [], failures: [], unmatchedSourceTables: [], error: null,
        confidenceThreshold: 0.8,
      });

      const status = await promise;
      expect(status.state).toBe('ready');
      expect(service.mapping()).toEqual(mapping);
      expect(service.status()?.state).toBe('ready');
      // The server-configured threshold, not a client-side default, drives the review grid.
      expect(service.confidenceThreshold()).toBe(0.8);
      // Closed by the client on the terminal event, so the browser never reconnects to a finished run.
      expect(opened[0].closed).toBe(true);
      // No polling: the whole run cost the browser exactly one request.
      expect(opened.length).toBe(1);
      http.expectNone('/api/analyse/sess-1');
    });

    it('resolves with the failure reason once failed', async () => {
      service.sessionId.set('sess-2');

      const promise = service.watchUntilDone();

      opened[0].push(runningStatus('Validating'));
      opened[0].push({
        state: 'failed', step: null, mapping: null, issues: [], failures: ['boom'],
        unmatchedSourceTables: [], error: 'The source database is unreachable.', confidenceThreshold: 0.75,
      });

      const status = await promise;
      expect(status.state).toBe('failed');
      expect(status.error).toBe('The source database is unreachable.');
      expect(service.status()?.state).toBe('failed');
      expect(opened[0].closed).toBe(true);
    });

    it('rejects when the stream drops, instead of waiting forever', async () => {
      service.sessionId.set('sess-3');

      const promise = service.watchUntilDone();
      // Prevent an unhandled-rejection warning while the assertion below awaits the rejection.
      promise.catch(() => {});

      opened[0].push(runningStatus('Matching tables'));
      opened[0].drop();

      await expect(promise).rejects.toThrow(/Lost the connection/);
      expect(opened[0].closed).toBe(true);
    });
  });

  describe('accept/reject governs what is submitted', () => {
    beforeEach(() => {
      service.sessionId.set('sess-review');
      service.mapping.set(reviewMapping);
      service.confidenceThreshold.set(0.75);
    });

    it('unticking a proposal removes it from the mapping sent to the server', async () => {
      // ClientId (confidence 0.99) is accepted by default until unticked.
      service.toggleAccepted('dbo.Client', 'ClientId');

      const promise = service.generateScript();
      const request = http.expectOne('/api/script/generate');
      const table = (request.request.body as { mapping: Mapping }).mapping.tables[0];

      expect(table.columns.map(c => c.targetColumn)).toEqual(['FullName']);
      expect(table.unmapped).toContainEqual({ targetColumn: 'ClientId', reason: 'Rejected in review.' });

      request.flush({ sql: '', issues: [] });
      await promise;
    });

    it('re-ticking a proposal restores it to the mapping sent to the server', async () => {
      service.toggleAccepted('dbo.Client', 'ClientId');
      service.toggleAccepted('dbo.Client', 'ClientId');

      const promise = service.generateScript();
      const request = http.expectOne('/api/script/generate');
      const table = (request.request.body as { mapping: Mapping }).mapping.tables[0];

      expect(table.columns.map(c => c.targetColumn)).toEqual(['ClientId', 'FullName']);
      expect(table.unmapped).toEqual([{ targetColumn: 'Notes', reason: 'No source column.' }]);

      request.flush({ sql: '', issues: [] });
      await promise;
    });

    it('also drops an unticked proposal from the mapping XML save request', async () => {
      service.toggleAccepted('dbo.Client', 'ClientId');

      const promise = service.saveMapping();
      const request = http.expectOne('/api/mapping/save');
      const table = (request.request.body as { mapping: Mapping }).mapping.tables[0];

      expect(table.columns.map(c => c.targetColumn)).toEqual(['FullName']);

      request.flush({ path: 'p', sha256: 'x'.repeat(64) });
      await promise;
    });

    it('a proposal left unaccepted below the confidence threshold is also dropped, never ticked', async () => {
      service.confidenceThreshold.set(0.95); // now only ClientId (0.99) clears the bar by default

      const promise = service.generateScript();
      const request = http.expectOne('/api/script/generate');
      const table = (request.request.body as { mapping: Mapping }).mapping.tables[0];

      expect(table.columns.map(c => c.targetColumn)).toEqual(['ClientId']);
      expect(table.unmapped).toContainEqual({ targetColumn: 'FullName', reason: 'Rejected in review.' });

      request.flush({ sql: '', issues: [] });
      await promise;
    });
  });
});
