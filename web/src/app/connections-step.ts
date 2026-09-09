import { Component, inject, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MigrationService } from './migration.service';
import { ConnectionRequest, ConnectionTestResponse } from './models';
import { describeError } from './http-error';

@Component({
  selector: 'app-connections-step',
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatInputModule],
  template: `
    <div class="panels">
      @for (side of ['source', 'target']; track side) {
        <section class="panel">
          <h3>{{ side === 'source' ? 'Source' : 'Target' }}</h3>
          <mat-form-field>
            <mat-label>Server</mat-label>
            <input matInput [ngModel]="server(side)" (ngModelChange)="setServer(side, $event)" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>Database</mat-label>
            <input matInput [ngModel]="database(side)" (ngModelChange)="setDatabase(side, $event)" />
          </mat-form-field>
          <button mat-stroked-button (click)="test(side)">Test</button>
          @if (result(side); as r) {
            <p [class.error]="!r.ok">{{ r.ok ? r.version + ' · ' + r.tableCount + ' tables' : r.error }}</p>
          }
        </section>
      }
    </div>

    <mat-form-field class="wide">
      <mat-label>Source reference used in the generated script</mat-label>
      <input matInput [(ngModel)]="sourceReference" />
      <mat-hint>Same instance: [DatabaseName]. Different instance: [LinkedServer].[DatabaseName].</mat-hint>
    </mat-form-field>

    <button mat-flat-button [disabled]="!bothTested()" (click)="start()">Analyse</button>
    @if (startError(); as message) {
      <p class="error">{{ message }}</p>
    }
  `,
  styles: `
    .panels { display: flex; gap: 2rem; }
    .panel { display: flex; flex-direction: column; gap: 0.5rem; min-width: 20rem; }
    .wide { width: 100%; margin-top: 1rem; }
    .error { color: var(--mat-sys-error); }
  `,
})
export class ConnectionsStep {
  private readonly service = inject(MigrationService);

  readonly started = output<void>();

  sourceReference = '';

  private readonly connections = signal<Record<string, ConnectionRequest>>({
    source: { server: 'localhost', database: '' },
    target: { server: 'localhost', database: '' },
  });
  private readonly results = signal<Record<string, ConnectionTestResponse | undefined>>({});
  readonly startError = signal<string | null>(null);

  server = (side: string) => this.connections()[side].server;
  database = (side: string) => this.connections()[side].database;
  result = (side: string) => this.results()[side];

  setServer(side: string, value: string) {
    this.connections.update(c => ({ ...c, [side]: { ...c[side], server: value } }));
  }

  setDatabase(side: string, value: string) {
    this.connections.update(c => ({ ...c, [side]: { ...c[side], database: value } }));
    if (side === 'source') this.sourceReference = `[${value}]`;
  }

  async test(side: string) {
    try {
      const result = await this.service.testConnection(this.connections()[side]);
      this.results.update(r => ({ ...r, [side]: result }));
    } catch (err) {
      this.results.update(r => ({
        ...r,
        [side]: { ok: false, version: null, tableCount: 0, error: describeError(err) },
      }));
    }
  }

  bothTested = () => this.results()['source']?.ok === true && this.results()['target']?.ok === true;

  async start() {
    this.startError.set(null);
    try {
      const { source, target } = this.connections();
      await this.service.analyse(source, target, this.sourceReference);
      this.started.emit();
    } catch (err) {
      this.startError.set(describeError(err));
    }
  }
}
