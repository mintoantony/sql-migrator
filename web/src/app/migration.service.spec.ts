import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { MigrationService } from './migration.service';
import { Mapping, SessionStatus } from './models';

function runningStatus(step: string): SessionStatus {
  return { state: 'running', step, mapping: null, issues: [], failures: [], unmatchedSourceTables: [], error: null };
}

describe('MigrationService', () => {
  let service: MigrationService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [MigrationService, provideHttpClient(), provideHttpClientTesting()],
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

  describe('pollUntilDone', () => {
    it('polls while running, then resolves and stores the mapping once ready', async () => {
      vi.useFakeTimers();
      service.sessionId.set('sess-1');
      const mapping: Mapping = {
        name: 'Demo', model: 'test', sourceDatabase: 'S', sourceReference: '[S]', targetDatabase: 'T', tables: [],
      };

      const promise = service.pollUntilDone(1000);

      http.expectOne('/api/analyse/sess-1').flush(runningStatus('Matching tables'));
      await Promise.resolve();
      expect(service.status()?.state).toBe('running');

      await vi.advanceTimersByTimeAsync(1000);

      http.expectOne('/api/analyse/sess-1').flush({
        state: 'ready', step: null, mapping, issues: [], failures: [], unmatchedSourceTables: [], error: null,
      });

      const status = await promise;
      expect(status.state).toBe('ready');
      expect(service.mapping()).toEqual(mapping);
      expect(service.status()?.state).toBe('ready');
    });

    it('polls while running, then resolves with the failure reason once failed', async () => {
      vi.useFakeTimers();
      service.sessionId.set('sess-2');

      const promise = service.pollUntilDone(1000);

      http.expectOne('/api/analyse/sess-2').flush(runningStatus('Validating'));
      await vi.advanceTimersByTimeAsync(1000);

      http.expectOne('/api/analyse/sess-2').flush({
        state: 'failed', step: null, mapping: null, issues: [], failures: ['boom'],
        unmatchedSourceTables: [], error: 'The source database is unreachable.',
      });

      const status = await promise;
      expect(status.state).toBe('failed');
      expect(status.error).toBe('The source database is unreachable.');
      expect(service.status()?.state).toBe('failed');
    });

    it('rejects when a request fails mid-poll, instead of looping forever', async () => {
      vi.useFakeTimers();
      service.sessionId.set('sess-3');

      const promise = service.pollUntilDone(1000);
      // Prevent an unhandled-rejection warning while the assertion below awaits the rejection.
      promise.catch(() => {});

      http.expectOne('/api/analyse/sess-3').flush(runningStatus('Matching tables'));
      await vi.advanceTimersByTimeAsync(1000);

      http.expectOne('/api/analyse/sess-3').flush('Internal error', { status: 500, statusText: 'Server Error' });

      await expect(promise).rejects.toBeInstanceOf(HttpErrorResponse);
    });
  });
});
