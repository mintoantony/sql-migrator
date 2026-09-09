import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { MigrationService } from './migration.service';

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

  afterEach(() => http.verify());

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
});
