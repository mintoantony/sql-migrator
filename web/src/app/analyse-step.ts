import { Component, inject, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MigrationService } from './migration.service';
import { describeError } from './http-error';

@Component({
  selector: 'app-analyse-step',
  imports: [MatButtonModule, MatProgressBarModule],
  template: `
    @if (pollError(); as message) {
      <p class="error">{{ message }}</p>
      <button mat-stroked-button (click)="back.emit()">Back to connections</button>
    } @else if (service.status(); as status) {
      @if (status.state === 'running') {
        <mat-progress-bar mode="indeterminate" />
        <p>{{ status.step ?? 'Working…' }}</p>
      } @else if (status.state === 'failed') {
        <p class="error">Analysis failed: {{ status.error }}</p>
        <button mat-stroked-button (click)="back.emit()">Back to connections</button>
      }
    } @else {
      <mat-progress-bar mode="indeterminate" />
      <p>Starting…</p>
    }
  `,
  styles: `.error { color: var(--mat-sys-error); }`,
})
export class AnalyseStep {
  readonly service = inject(MigrationService);
  readonly finished = output<void>();
  readonly back = output<void>();

  readonly pollError = signal<string | null>(null);

  async ngOnInit() {
    try {
      const status = await this.service.watchUntilDone();
      if (status.state === 'ready') this.finished.emit();
    } catch (err) {
      this.pollError.set(describeError(err));
    }
  }
}
