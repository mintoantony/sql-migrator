import { Component, inject, output } from '@angular/core';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MigrationService } from './migration.service';

@Component({
  selector: 'app-analyse-step',
  imports: [MatProgressBarModule],
  template: `
    @if (service.status(); as status) {
      @if (status.state === 'running') {
        <mat-progress-bar mode="indeterminate" />
        <p>{{ status.step ?? 'Working…' }}</p>
      } @else if (status.state === 'failed') {
        <p class="error">Analysis failed: {{ status.error }}</p>
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

  async ngOnInit() {
    const status = await this.service.pollUntilDone();
    if (status.state === 'ready') this.finished.emit();
  }
}
