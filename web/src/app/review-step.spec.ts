import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { MigrationService } from './migration.service';
import { ReviewStep } from './review-step';
import { Mapping } from './models';

const mapping: Mapping = {
  name: 'Demo', model: 'test', sourceDatabase: 'S', sourceReference: '[S]', targetDatabase: 'T',
  tables: [{
    sourceTable: 'dbo.Customer', targetTable: 'dbo.Client', origin: 'ai', confidence: 0.96, reason: 'Customers.',
    columns: [
      { targetColumn: 'ClientId', rule: 'copy', expression: 'CustomerId', origin: 'ai', confidence: 0.99, reason: 'Key.' },
      { targetColumn: 'FullName', rule: 'concat', expression: "CONCAT(FirstName, ' ', LastName)", origin: 'ai', confidence: 0.6, reason: 'Two parts.' },
    ],
    unmapped: [{ targetColumn: 'Notes', reason: 'No source column.' }],
  }],
};

describe('ReviewStep', () => {
  let fixture: ComponentFixture<ReviewStep>;
  let service: MigrationService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [ReviewStep],
      providers: [MigrationService, provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(MigrationService);
    service.mapping.set(mapping);
    service.issues.set([]);
    fixture = TestBed.createComponent(ReviewStep);
    fixture.detectChanges();
  });

  it('lists every mapped column of the selected table', () => {
    const text = fixture.nativeElement.textContent;
    expect(text).toContain('ClientId');
    expect(text).toContain('FullName');
  });

  it('shows unmapped target columns, because what was not mapped matters', () => {
    expect(fixture.nativeElement.textContent).toContain('Notes');
  });

  it('pre-accepts high confidence and leaves low confidence unaccepted', () => {
    expect(service.mapping()!.tables[0].columns.length).toBe(2);
    expect(fixture.componentInstance.accepted('dbo.Client', 'ClientId')).toBe(true);
    expect(fixture.componentInstance.accepted('dbo.Client', 'FullName')).toBe(false);
  });

  it('disables generate while a blocking issue stands', () => {
    service.issues.set([
      { code: 'EXP002', severity: 'Blocking', message: 'no', table: 'dbo.Client', column: 'FullName' },
    ]);
    fixture.detectChanges();

    expect(fixture.componentInstance.canGenerate()).toBe(false);
  });
});
