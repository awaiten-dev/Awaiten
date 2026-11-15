/**
 * Shows the code block of a diagnostic entry that reports the diagnostic `id` and swaps in the fixed code block
 * on request. Both blocks are children of the component, the mistake first.
 */
import React, {Children, useState, type ReactElement, type ReactNode} from 'react';
import clsx from 'clsx';
import styles from './styles.module.css';

export default function ApplyFix({id, children}: {id: string; children: ReactNode}): ReactElement {
  const [mistake, fixed] = Children.toArray(children);
  const [isFixed, setFixed] = useState(false);
  return (
    <div className={clsx(styles.applyFix, isFixed && styles.isFixed)}>
      {isFixed ? fixed : mistake}
      <div className={styles.bar} aria-live="polite">
        <span className={styles.badge}>{isFixed ? 'FIXED' : id}</span>
        <span className={styles.status}>{isFixed ? 'No diagnostic.' : `This code reports ${id}.`}</span>
        <button type="button" className={styles.button} onClick={() => setFixed(!isFixed)}>
          {isFixed ? 'Undo' : 'Apply fix'}
        </button>
      </div>
    </div>
  );
}
