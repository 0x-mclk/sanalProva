import { CheckIcon } from './icons';
import classes from './StepProgress.module.css';

/**
 * Akış adım göstergesi (referans: "✓ Fotoğraf · 2 Prova türü · 3 Kıyafet").
 * Tamamlanan ve etkin adımın çizgisi bordo, sonrakiler açık krem.
 */
export function StepProgress({ steps, current }: { steps: readonly string[]; current: number }) {
  return (
    <ol className={classes.root} aria-label={`Adım ${current + 1} / ${steps.length}`}>
      {steps.map((label, index) => {
        const state = index < current ? 'done' : index === current ? 'active' : 'todo';
        return (
          <li key={label} className={classes.step} data-state={state} aria-current={state === 'active' ? 'step' : undefined}>
            <span className={classes.bar} aria-hidden="true" />
            <span className={classes.label}>
              {state === 'done' ? <CheckIcon size={12} /> : <span className={classes.index}>{index + 1}</span>}
              {label}
            </span>
          </li>
        );
      })}
    </ol>
  );
}
