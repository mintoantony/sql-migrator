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

  it('lists every mapped column, one aligned row each', () => {
    const rows = fixture.nativeElement.querySelectorAll('table.grid tbody tr');
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('ClientId');
    expect(rows[1].textContent).toContain('FullName');
  });

  it('puts the table summary in the panel header, where the left-hand list used to be', () => {
    const header = fixture.nativeElement.querySelector('mat-expansion-panel-header');
    expect(header.textContent).toContain('dbo.Customer');
    expect(header.textContent).toContain('dbo.Client');
    expect(header.textContent).toContain('96%');
    expect(header.textContent).toContain('Customers.');
  });

  it('edits the expression in a plain input that sits inside the cell', () => {
    const input = fixture.nativeElement.querySelector('td.expression input') as HTMLInputElement;
    expect(input.value).toBe('CustomerId');
  });

  it('shows a green tick with the model reason when a line has no issue', () => {
    const status = fixture.componentInstance.lineStatus('dbo.Client', mapping.tables[0].columns[0]);
    expect(status.tone).toBe('ok');
    expect(status.icon).toBe('check_circle');
    expect(status.message).toBe('Key.');

    const button = fixture.nativeElement.querySelector('td.status button');
    expect(button.classList).toContain('ok');
    expect(button.textContent).toContain('check_circle');
  });

  it('shows a red error icon, the code and the message when a line has a blocking issue', () => {
    service.issues.set([
      { code: 'EXP002', severity: 'Blocking', message: 'Invalid column name', table: 'dbo.Client', column: 'FullName' },
    ]);
    fixture.detectChanges();

    const status = fixture.componentInstance.lineStatus('dbo.Client', mapping.tables[0].columns[1]);
    expect(status.tone).toBe('error');
    expect(status.icon).toBe('error');
    expect(status.title).toContain('EXP002');
    expect(status.message).toBe('Invalid column name');

    const buttons = fixture.nativeElement.querySelectorAll('td.status button');
    expect(buttons[1].classList).toContain('error');
    expect(buttons[1].getAttribute('aria-label')).toContain('Invalid column name');
    // The header carries the count so a collapsed table still shows it needs attention.
    expect(fixture.nativeElement.querySelector('mat-expansion-panel-header').textContent).toContain('1 blocking');
  });

  it('shows an amber warning icon for a warning', () => {
    service.issues.set([
      { code: 'TYP002', severity: 'Warning', message: 'nvarchar(201) into nvarchar(200)', table: 'dbo.Client', column: 'FullName' },
    ]);
    fixture.detectChanges();

    const status = fixture.componentInstance.lineStatus('dbo.Client', mapping.tables[0].columns[1]);
    expect(status.tone).toBe('warning');
    expect(status.icon).toBe('warning');
  });

  it('shows unmapped target columns, because what was not mapped matters', () => {
    expect(fixture.nativeElement.textContent).toContain('Notes');
  });

  it('pre-accepts high confidence and leaves low confidence unaccepted', () => {
    expect(service.mapping()!.tables[0].columns.length).toBe(2);
    expect(fixture.componentInstance.accepted('dbo.Client', 'ClientId')).toBe(true);
    expect(fixture.componentInstance.accepted('dbo.Client', 'FullName')).toBe(false);
  });

  it('toggling a checkbox flips accept state, and toggling again restores it', () => {
    const component = fixture.componentInstance;

    expect(component.accepted('dbo.Client', 'ClientId')).toBe(true);
    component.toggle('dbo.Client', 'ClientId');
    expect(component.accepted('dbo.Client', 'ClientId')).toBe(false);
    component.toggle('dbo.Client', 'ClientId');
    expect(component.accepted('dbo.Client', 'ClientId')).toBe(true);
  });

  it('disables generate while a blocking issue stands', () => {
    service.issues.set([
      { code: 'EXP002', severity: 'Blocking', message: 'no', table: 'dbo.Client', column: 'FullName' },
    ]);
    fixture.detectChanges();

    expect(fixture.componentInstance.canGenerate()).toBe(false);
  });
});
