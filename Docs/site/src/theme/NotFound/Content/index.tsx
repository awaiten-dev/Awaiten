/**
 * Swizzled (ejected) NotFound content: instead of the stock dead end, it offers the pages most visitors look for.
 */
import React, {type ReactNode} from 'react';
import clsx from 'clsx';
import Link from '@docusaurus/Link';
import Heading from '@theme/Heading';
import type {Props} from '@theme/NotFound/Content';
import styles from './styles.module.css';

const destinations = [
  {title: 'Getting started', text: 'Build your first coffee shop from scratch.', to: '/docs/getting-started'},
  {title: 'Diagnostics', text: 'Every AWT diagnostic, with an example and a fix.', to: '/docs/diagnostics'},
  {title: 'Benchmarks', text: 'Awaiten against MS.DI, Autofac, DryIoc and Simple Injector.', to: '/docs/benchmarks'},
];

export default function NotFoundContent({className}: Props): ReactNode {
  return (
    <main className={clsx('container margin-vert--xl', className)}>
      <div className="row">
        <div className="col col--6 col--offset-3">
          <Heading as="h1">Page not found</Heading>
          <p>This page does not exist, or it has moved. Continue with one of these, or use the search at the top.</p>
          <div className={styles.destinations}>
            {destinations.map((destination) => (
              <Link key={destination.to} to={destination.to} className={styles.destination}>
                <span className={styles.title}>{destination.title} →</span>
                <span className={styles.text}>{destination.text}</span>
              </Link>
            ))}
          </div>
        </div>
      </div>
    </main>
  );
}
