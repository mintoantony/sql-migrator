import { Component, computed, inject, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MigrationService } from './migration.service';
import { ColumnMapping, Issue, TableMapping } from './models';
import { describeError } from './http-error';

/** What the status column says about one line: the icon, its colour class, and the popover text. */
export interface LineStatus {
  icon: 'check_circle' | 'info' | 'warning' | 'error';
  tone: 'ok' | 'info' | 'warning' | 'error';
  title: string;
  message: string;
}

@Component({
  selector: 'app-review-step',
  imports: [MatButtonModule, MatCheckboxModule, MatExpansionModule, MatIconModule, MatMenuModule],
  template: `
    <p class="verdict">
      {{ service.mapping()?.tables?.length ?? 0 }} table pairs,
      {{ service.blockingCount() }} blocking, {{ service.warningCount() }} warnings
    </p>

    <!--
      One panel per table pair. The header is the summary the old left-hand list carried; the
      body is a real table, so every column lines up from row to row and a row is the height
      of a line of text. The expression is editable in place: the input has no chrome until it
      is focused, so the grid reads as data rather than as a form.
    -->
    <mat-accordion multi>
      @for (table of service.mapping()?.tables ?? []; track table.targetTable; let first = $first) {
        <mat-expansion-panel [expanded]="first">
          <mat-expansion-panel-header>
            <mat-panel-title>
              <span class="pair">{{ table.sourceTable }}<mat-icon inline>arrow_forward</mat-icon>{{ table.targetTable }}</span>
            </mat-panel-title>
            <mat-panel-description>
              <span class="summary">{{ percent(table.confidence) }} {{ table.reason }}</span>
              @if (tableCounts(table.targetTable); as counts) {
                @if (counts.blocking) { <span class="count error">{{ counts.blocking }} blocking</span> }
                @if (counts.warnings) { <span class="count warning">{{ counts.warnings }} {{ counts.warnings === 1 ? 'warning' : 'warnings' }}</span> }
              }
            </mat-panel-description>
          </mat-expansion-panel-header>

          <table class="grid">
            <thead>
              <tr>
                <th class="accept"></th>
                <th>Target column</th>
                <th>Rule</th>
                <th>Expression</th>
                <th class="confidence">Confidence</th>
                <th class="status"></th>
              </tr>
            </thead>
            <tbody>
              @for (column of table.columns; track column.targetColumn) {
                @let status = lineStatus(table.targetTable, column);
                <tr>
                  <td class="accept">
                    <mat-checkbox
                      [checked]="accepted(table.targetTable, column.targetColumn)"
                      (change)="toggle(table.targetTable, column.targetColumn)"
                      [aria-label]="'Accept ' + column.targetColumn" />
                  </td>
                  <td class="name">{{ column.targetColumn }}</td>
                  <td class="rule">{{ column.rule }}</td>
                  <td class="expression">
                    <input
                      [value]="column.expression"
                      (change)="edit(table, column, $any($event.target).value)"
                      [attr.aria-label]="'Expression for ' + column.targetColumn"
                      spellcheck="false" />
                  </td>
                  <td class="confidence">{{ percent(column.confidence) }}</td>
                  <td class="status">
                    <button mat-icon-button class="status-button" [class]="'status-button ' + status.tone"
                            [matMenuTriggerFor]="popover" [attr.aria-label]="status.title + ': ' + status.message">
                      <mat-icon>{{ status.icon }}</mat-icon>
                    </button>
                    <mat-menu #popover="matMenu" class="status-popover">
                      <div class="popover" (click)="$event.stopPropagation()">
                        <strong [class]="'popover-title ' + status.tone">{{ status.title }}</strong>
                        <p>{{ status.message }}</p>
                      </div>
                    </mat-menu>
                  </td>
                </tr>
              }
            </tbody>
          </table>

          @if (table.unmapped.length) {
            <p class="unmapped">
              <span class="unmapped-label">Receiving nothing:</span>
              @for (u of table.unmapped; track u.targetColumn; let last = $last) {
                <span>{{ u.targetColumn }} ({{ u.reason }}){{ last ? '' : ', ' }}</span>
              }
            </p>
          }
        </mat-expansion-panel>
      }
    </mat-accordion>

    @if (service.status()?.unmatchedSourceTables?.length) {
      <h4>Source tables matched to nothing</h4>
      <p>{{ service.status()!.unmatchedSourceTables.join(', ') }}</p>
    }

    <!--
      The analysis records why each table failed — a provider timeout, a 429, a response it
      could not parse — and this used to render none of it. A run that hit its rate limit
      therefore arrived here as an empty grid with no explanation, which reads as "the tool
      is broken" rather than "the model provider refused, try again". These are the whole
      reason the grid is empty; they belong on the screen.
    -->
    @if (service.status()?.failures?.length) {
      <h4>The model could not be reached for some tables</h4>
      <ul class="failures">
        @for (failure of service.status()!.failures; track $index) {
          <li>{{ failure }}</li>
        }
      </ul>
      <p class="hint">These tables were left unmapped. Re-running the analysis usually picks them up.</p>
    }

    @if (editError(); as message) {
      <p class="error">{{ message }}</p>
    }

    <button mat-flat-button class="generate" [disabled]="!canGenerate()" (click)="generate.emit()">Generate script</button>
  `,
  styles: `
    :host { display: block; }
    .verdict { font-weight: 600; margin: 0 0 1rem; }

    mat-expansion-panel { margin-bottom: 0.5rem; }
    .pair { display: inline-flex; align-items: center; gap: 0.35rem; font-weight: 500; white-space: nowrap; }
    .pair mat-icon { font-size: 1rem; width: 1rem; height: 1rem; opacity: 0.6; }
    mat-panel-description { display: flex; align-items: center; gap: 0.75rem; min-width: 0; }
    .summary { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .count { font-size: 0.8rem; font-weight: 500; white-space: nowrap; }
    .count.error { color: var(--mat-sys-error); }
    .count.warning { color: var(--tone-warning); }

    /*
      A native table rather than mat-table: the point is alignment and a row the height of a
      line of text, and a plain table gives both with no component chrome to fight.
    */
    .grid { width: 100%; border-collapse: collapse; font-size: 0.9rem; }
    .grid th {
      text-align: left; font-weight: 500; font-size: 0.75rem; color: var(--mat-sys-on-surface-variant);
      padding: 0.25rem 0.5rem; border-bottom: 1px solid var(--mat-sys-outline-variant);
    }
    .grid td { padding: 0 0.5rem; height: 2rem; border-bottom: 1px solid var(--mat-sys-surface-container-high); vertical-align: middle; }
    .grid tbody tr:hover td { background: var(--mat-sys-surface-container-low); }
    .grid .accept { width: 2.25rem; padding-right: 0; }
    .grid .name { white-space: nowrap; font-weight: 500; }
    .grid .rule { white-space: nowrap; color: var(--mat-sys-on-surface-variant); }
    .grid .expression { width: 55%; }
    .grid .confidence { width: 5.5rem; text-align: right; font-variant-numeric: tabular-nums; }
    .grid .status { width: 2.5rem; text-align: center; padding: 0; }

    /* Checkbox sized to the row, not to a touch target. */
    mat-checkbox { --mdc-checkbox-state-layer-size: 28px; display: block; margin-left: -0.35rem; }

    /* The expression looks like a cell until it is focused, then like a field. */
    .expression input {
      width: 100%; box-sizing: border-box; margin: 0; padding: 0.15rem 0.25rem;
      font: inherit; font-family: Consolas, ui-monospace, 'SFMono-Regular', Menlo, monospace; font-size: 0.85rem;
      color: inherit; background: transparent; border: 0; border-bottom: 1px solid transparent; border-radius: 2px 2px 0 0;
      outline: none;
    }
    .expression input:hover { background: var(--mat-sys-surface-container); }
    .expression input:focus { background: var(--mat-sys-surface-container); border-bottom-color: var(--mat-sys-primary); }

    /* One icon per line carries the verdict; its colour is the only colour in the grid. */
    .status-button { width: 2rem; height: 2rem; padding: 0; --mdc-icon-button-state-layer-size: 2rem; }
    .status-button mat-icon { font-size: 1.25rem; width: 1.25rem; height: 1.25rem; }
    .status-button.ok { color: var(--tone-ok); }
    .status-button.info { color: var(--mat-sys-primary); }
    .status-button.warning { color: var(--tone-warning); }
    .status-button.error { color: var(--mat-sys-error); }

    /* Rendered in the overlay, but still carries this component's scoping attribute. */
    .popover { padding: 0.6rem 1rem 0.75rem; max-width: 24rem; font-size: 0.9rem; line-height: 1.4; }
    .popover p { margin: 0.3rem 0 0; }
    .popover-title.ok { color: var(--tone-ok); }
    .popover-title.info { color: var(--mat-sys-primary); }
    .popover-title.warning { color: var(--tone-warning); }
    .popover-title.error { color: var(--mat-sys-error); }

    .unmapped { margin: 0.75rem 0 0; font-size: 0.85rem; color: var(--mat-sys-on-surface-variant); }
    .unmapped-label { font-weight: 500; margin-right: 0.25rem; }

    h4 { margin: 1.25rem 0 0.25rem; }
    .error { color: var(--mat-sys-error); }
    .failures { margin: 0 0 0.5rem; padding-left: 1.25rem; color: var(--mat-sys-error); }
    .failures li { margin-bottom: 0.25rem; }
    .hint { margin-top: 0; opacity: 0.8; }
    .generate { margin-top: 1.25rem; }

    :host { --tone-ok: #2e7d32; --tone-warning: #9a6700; }
  `,
})
export class ReviewStep {
  readonly service = inject(MigrationService);
  readonly generate = output<void>();

  readonly editError = signal<string | null>(null);

  readonly canGenerate = computed(() => !this.service.hasBlocking());

  percent(value: number | null): string {
    return value === null ? '' : `${Math.round(value * 100)}%`;
  }

  /**
   * The accept state governs what actually reaches the saved mapping and the generated script
   * — see MigrationService.mappingToSubmit. This component only renders it and lets it be toggled.
   */
  accepted(targetTable: string, targetColumn: string): boolean {
    return this.service.accepted(targetTable, targetColumn);
  }

  toggle(targetTable: string, targetColumn: string) {
    this.service.toggleAccepted(targetTable, targetColumn);
  }

  issueFor(targetTable: string, targetColumn: string): Issue | undefined {
    return this.service.issues().find(i => i.table === targetTable && i.column === targetColumn);
  }

  /** Blocking and warning totals for one table pair, shown in its panel header. */
  tableCounts(targetTable: string): { blocking: number; warnings: number } | null {
    const issues = this.service.issues().filter(i => i.table === targetTable);
    const blocking = issues.filter(i => i.severity === 'Blocking').length;
    const warnings = issues.filter(i => i.severity === 'Warning').length;
    return blocking || warnings ? { blocking, warnings } : null;
  }

  /**
   * The worst issue on the line wins the icon. A clean line shows a green tick and the model's
   * own reason for the proposal, so the popover always has something worth saying.
   */
  lineStatus(targetTable: string, column: ColumnMapping): LineStatus {
    const issue = this.issueFor(targetTable, column.targetColumn);
    if (!issue) {
      return {
        icon: 'check_circle', tone: 'ok', title: 'Looks right',
        message: column.reason ?? 'No issues found.',
      };
    }
    const tone = issue.severity === 'Blocking' ? 'error' : issue.severity === 'Warning' ? 'warning' : 'info';
    const icon = tone === 'error' ? 'error' : tone === 'warning' ? 'warning' : 'info';
    const title = issue.severity === 'Blocking' ? `${issue.code}: must be fixed`
      : issue.severity === 'Warning' ? `${issue.code}: check this` : issue.code;
    return { icon, tone, title, message: issue.message };
  }

  /** Editing an expression marks the line as human-authored and re-validates it against the source. */
  async edit(table: TableMapping, column: ColumnMapping, expression: string) {
    if (expression === column.expression) return;
    this.applyEdit(table.targetTable, column.targetColumn, expression);
    this.editError.set(null);

    try {
      const result = await this.service.validateExpression(
        table.sourceTable, table.targetTable, column.targetColumn, expression);

      const others = this.service.issues()
        .filter(i => !(i.table === table.targetTable && i.column === column.targetColumn));
      this.service.issues.set([...others, ...result.issues]);
    } catch (err) {
      this.editError.set(describeError(err));
    }
  }

  /** Replaces the edited column immutably so the `mapping` signal's identity changes on every edit. */
  private applyEdit(targetTable: string, targetColumn: string, expression: string) {
    this.service.mapping.update(mapping => {
      if (!mapping) return mapping;
      return {
        ...mapping,
        tables: mapping.tables.map(t => t.targetTable !== targetTable ? t : {
          ...t,
          columns: t.columns.map(c =>
            c.targetColumn !== targetColumn ? c : { ...c, expression, origin: 'human' as const }),
        }),
      };
    });
  }
}
