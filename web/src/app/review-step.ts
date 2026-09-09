import { Component, computed, inject, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatListModule } from '@angular/material/list';
import { MigrationService } from './migration.service';
import { ColumnMapping, TableMapping } from './models';

const CONFIDENCE_THRESHOLD = 0.75;

@Component({
  selector: 'app-review-step',
  imports: [FormsModule, MatButtonModule, MatCheckboxModule, MatChipsModule,
            MatFormFieldModule, MatInputModule, MatListModule],
  template: `
    <p class="verdict">
      {{ service.mapping()?.tables?.length ?? 0 }} table pairs ·
      {{ service.blockingCount() }} blocking · {{ service.warningCount() }} warnings
    </p>

    <div class="layout">
      <mat-nav-list class="tables">
        @for (table of service.mapping()?.tables ?? []; track table.targetTable) {
          <a mat-list-item (click)="select(table)" [class.selected]="table === selected()">
            <span matListItemTitle>{{ table.sourceTable }} → {{ table.targetTable }}</span>
            <span matListItemLine>{{ percent(table.confidence) }} · {{ table.reason }}</span>
          </a>
        }
      </mat-nav-list>

      <div class="columns">
        @if (selected(); as table) {
          @for (column of table.columns; track column.targetColumn) {
            <div class="row">
              <mat-checkbox
                [checked]="accepted(table.targetTable, column.targetColumn)"
                (change)="toggle(table.targetTable, column.targetColumn)">
                {{ column.targetColumn }}
              </mat-checkbox>

              <mat-chip>{{ column.rule }}</mat-chip>

              <mat-form-field class="expression">
                <input matInput
                       [ngModel]="column.expression"
                       (ngModelChange)="edit(table, column, $event)" />
              </mat-form-field>

              <span class="confidence">{{ percent(column.confidence) }}</span>
              <span class="status" [class.bad]="issueFor(table.targetTable, column.targetColumn) !== undefined">
                {{ issueFor(table.targetTable, column.targetColumn)?.message ?? column.reason }}
              </span>
            </div>
          }

          @if (table.unmapped.length) {
            <h4>Target columns receiving nothing</h4>
            @for (u of table.unmapped; track u.targetColumn) {
              <p class="unmapped">{{ u.targetColumn }} — {{ u.reason }}</p>
            }
          }
        }
      </div>
    </div>

    @if (service.status()?.unmatchedSourceTables?.length) {
      <h4>Source tables matched to nothing</h4>
      <p>{{ service.status()!.unmatchedSourceTables.join(', ') }}</p>
    }

    <button mat-flat-button [disabled]="!canGenerate()" (click)="generate.emit()">Generate script</button>
  `,
  styles: `
    .layout { display: flex; gap: 1.5rem; }
    .tables { width: 24rem; border-right: 1px solid var(--mat-sys-outline-variant); }
    .columns { flex: 1; }
    .row { display: flex; align-items: center; gap: 0.75rem; }
    .expression { flex: 1; min-width: 24rem; }
    .confidence { font-variant-numeric: tabular-nums; width: 3rem; }
    .status.bad { color: var(--mat-sys-error); }
    .selected { background: var(--mat-sys-surface-variant); }
    .verdict { font-weight: 600; }
  `,
})
export class ReviewStep {
  readonly service = inject(MigrationService);
  readonly generate = output<void>();

  private readonly manualAccepts = signal<Record<string, boolean>>({});
  readonly selected = signal<TableMapping | null>(this.service.mapping()?.tables?.[0] ?? null);

  readonly canGenerate = computed(() => !this.service.hasBlocking());

  select(table: TableMapping) { this.selected.set(table); }

  percent(value: number | null): string {
    return value === null ? '—' : `${Math.round(value * 100)}%`;
  }

  /** High-confidence proposals arrive accepted; anything below needs a deliberate click. */
  accepted(targetTable: string, targetColumn: string): boolean {
    const key = `${targetTable}.${targetColumn}`;
    const manual = this.manualAccepts()[key];
    if (manual !== undefined) return manual;

    const column = this.column(targetTable, targetColumn);
    return (column?.confidence ?? 0) >= CONFIDENCE_THRESHOLD;
  }

  toggle(targetTable: string, targetColumn: string) {
    const key = `${targetTable}.${targetColumn}`;
    const current = this.accepted(targetTable, targetColumn);
    this.manualAccepts.update(a => ({ ...a, [key]: !current }));
  }

  issueFor(targetTable: string, targetColumn: string) {
    return this.service.issues().find(i => i.table === targetTable && i.column === targetColumn);
  }

  /** Editing an expression marks the line as human-authored and re-validates it against the source. */
  async edit(table: TableMapping, column: ColumnMapping, expression: string) {
    column.expression = expression;
    column.origin = 'human';

    const result = await this.service.validateExpression(
      table.sourceTable, table.targetTable, column.targetColumn, expression);

    const others = this.service.issues()
      .filter(i => !(i.table === table.targetTable && i.column === column.targetColumn));
    this.service.issues.set([...others, ...result.issues]);
  }

  private column(targetTable: string, targetColumn: string): ColumnMapping | undefined {
    return this.service.mapping()?.tables
      .find(t => t.targetTable === targetTable)?.columns
      .find(c => c.targetColumn === targetColumn);
  }
}
