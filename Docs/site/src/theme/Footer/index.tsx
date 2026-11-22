/**
 * Swizzled (ejected) footer: a brand column with the logo and tagline beside the link columns of the theme config,
 * above the copyright.
 */
import React, {type ReactElement} from 'react';
import Link from '@docusaurus/Link';
import useBaseUrl from '@docusaurus/useBaseUrl';
import useDocusaurusContext from '@docusaurus/useDocusaurusContext';
import {useThemeConfig, type MultiColumnFooter} from '@docusaurus/theme-common';
import LinkItem from '@theme/Footer/LinkItem';

export default function Footer(): ReactElement | null {
  const {footer} = useThemeConfig();
  const {siteConfig} = useDocusaurusContext();
  const logo = useBaseUrl('/img/logo-dark.svg');
  if (!footer) {
    return null;
  }
  return (
    <footer className="footer footer--dark">
      <div className="container">
        <div className="footer__links">
          <div className="footer__col footer__brand">
            <Link to="/" className="footer__brand-logo">
              <img src={logo} alt="" width={88} height={88} />
              <span>
                <span className="footer__brand-name">{siteConfig.title}</span>
                <span className="footer__tagline">{siteConfig.tagline}</span>
              </span>
            </Link>
          </div>
          {(footer.links as MultiColumnFooter['links']).map((column) => (
            <div key={column.title} className="footer__col">
              <div className="footer__title">{column.title}</div>
              <ul className="footer__items clean-list">
                {column.items.map((item) => (
                  <li key={item.label} className="footer__item">
                    <LinkItem item={item} />
                  </li>
                ))}
              </ul>
            </div>
          ))}
        </div>
        <div className="footer__copyright">{footer.copyright}</div>
      </div>
    </footer>
  );
}
