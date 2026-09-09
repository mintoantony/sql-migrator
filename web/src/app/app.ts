import { Component, signal } from '@angular/core';
import { MatStepperModule } from '@angular/material/stepper';
import { AnalyseStep } from './analyse-step';
import { ConnectionsStep } from './connections-step';
import { ReviewStep } from './review-step';
import { ScriptStep } from './script-step';

@Component({
  selector: 'app-root',
  imports: [MatStepperModule, ConnectionsStep, AnalyseStep, ReviewStep, ScriptStep],
  templateUrl: './app.html',
  styles: `:host { display: block; padding: 2rem; max-width: 80rem; margin: 0 auto; }`,
})
export class App {
  readonly step = signal(0);
  next() { this.step.update(s => s + 1); }
  goTo(index: number) { this.step.set(index); }
}
