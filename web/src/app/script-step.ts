import { Component, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MigrationService } from './migration.service';

@Component({
  selector: 'app-script-step',
  imports: [MatButtonModule],
  template: `
    @if (error(); as message) {
      <p class="error">{{ message }}</p>
    }
    @if (sql(); as text) {
      <div class="actions">
        <button mat-stroked-button (click)="copy(text)">Copy</button>
        <button mat-stroked-button (click)="download(text)">Download .sql</button>
        <button mat-stroked-button (click)="save()">Save mapping XML</button>
        @if (savedPath(); as path) { <span>Saved to {{ path }}</span> }
      </div>
      <pre>{{ text }}</pre>
    }
  `,
  styles: `
    pre { overflow-x: auto; padding: 1rem; background: var(--mat-sys-surface-variant); }
    .actions { display: flex; gap: 0.5rem; align-items: center; margin-bottom: 1rem; }
    .error { color: var(--mat-sys-error); }
  `,
})
export class ScriptStep {
  private readonly service = inject(MigrationService);

  readonly sql = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  readonly savedPath = signal<string | null>(null);

  async ngOnInit() {
    try {
      const result = await this.service.generateScript();
      this.sql.set(result.sql);
    } catch {
      this.error.set('The mapping still has blocking issues. Go back and resolve them.');
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
    const result = await this.service.saveMapping();
    this.savedPath.set(result.path);
  }
}
