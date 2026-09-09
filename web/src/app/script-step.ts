import { Component, EventEmitter, inject, Output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { HttpErrorResponse } from '@angular/common/http';
import { MigrationService } from './migration.service';
import { describeError } from './http-error';
import { Issue } from './models';

@Component({
  selector: 'app-script-step',
  imports: [MatButtonModule],
  template: `
    @if (error(); as message) {
      <p class="error">{{ message }}</p>

      @if (blocking().length) {
        <ul class="blocking">
          @for (issue of blocking(); track $index) {
            <li>
              <strong>{{ issue.code }}</strong>
              @if (issue.table) {
                <span class="where">{{ issue.table }}@if (issue.column) {<span>.{{ issue.column }}</span>}</span>
              }
              — {{ issue.message }}
            </li>
          }
        </ul>
      }

      <button mat-stroked-button (click)="back.emit()">Back to review</button>
    }

    @if (sql(); as text) {
      <div class="actions">
        <button mat-stroked-button (click)="copy(text)">Copy</button>
        <button mat-stroked-button (click)="download(text)">Download .sql</button>
        <button mat-stroked-button (click)="save()">Save mapping XML</button>
        <button mat-stroked-button (click)="back.emit()">Back to review</button>
        @if (savedPath(); as path) { <span>Saved to {{ path }}</span> }
        @if (saveError(); as message) { <span class="error">{{ message }}</span> }
      </div>
      <pre>{{ text }}</pre>
    }
  `,
  styles: `
    pre { overflow-x: auto; padding: 1rem; background: var(--mat-sys-surface-variant); }
    .actions { display: flex; gap: 0.5rem; align-items: center; margin-bottom: 1rem; flex-wrap: wrap; }
    .error { color: var(--mat-sys-error); }
    .blocking { margin: 0 0 1rem; padding-left: 1.25rem; }
    .blocking li { margin-bottom: 0.25rem; }
    .where { font-family: monospace; }
  `,
})
export class ScriptStep {
  private readonly service = inject(MigrationService);

  /** Lets a human return to the grid — the only way out when generation is refused. */
  @Output() readonly back = new EventEmitter<void>();

  readonly sql = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  readonly blocking = signal<Issue[]>([]);
  readonly savedPath = signal<string | null>(null);
  readonly saveError = signal<string | null>(null);

  async ngOnInit() {
    try {
      const result = await this.service.generateScript();
      this.sql.set(result.sql);
    } catch (err) {
      // This used to be a bare `catch` that always reported "the mapping still has blocking
      // issues", whatever had actually gone wrong. That is a lie in every other case — a
      // backend that is down, a 500, a session the server has forgotten — and it sent people
      // to hunt for blocking issues in a grid that had none. Worse, the API returns the
      // offending issues in the 400 body and they were thrown away, so the one case where the
      // message was true still did not say WHICH issues.
      this.blocking.set(blockingIssuesFrom(err));
      this.error.set(this.blocking().length
        ? 'Generation was refused: the mapping still has blocking issues.'
        : describeError(err));
    }
  }

  copy(text: string) { void navigator.clipboard.writeText(text); }

  download(text: string) {
    const url = URL.createObjectURL(new Blob([text], { type: 'application/sql' }));
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = 'migration.sql';
    anchor.click();
    URL.revokeObjectURL(url);
  }

  async save() {
    this.saveError.set(null);
    try {
      const result = await this.service.saveMapping();
      this.savedPath.set(result.path);
    } catch (err) {
      this.saveError.set(describeError(err));
    }
  }
}

/** The blocking issues the API sent back with a refusal, or none if this was some other failure. */
function blockingIssuesFrom(err: unknown): Issue[] {
  if (err instanceof HttpErrorResponse && err.status === 400) {
    const issues = (err.error as { issues?: Issue[] } | null)?.issues;
    if (Array.isArray(issues)) return issues;
  }
  return [];
}
