import type {PrismTheme} from 'prism-react-renderer';

type Palette = {
  background: string;
  foreground: string;
  keyword: string;
  attribute: string;
  type: string;
  string: string;
  comment: string;
  inserted: string;
  deleted: string;
};

/** Builds a Prism theme from the code colors of the start page (src/pages/index.module.css), so code looks the same everywhere. */
function theme(palette: Palette): PrismTheme {
  return {
    plain: {color: palette.foreground, backgroundColor: palette.background},
    styles: [
      {types: ['comment', 'prolog', 'doctype', 'cdata'], style: {color: palette.comment}},
      {types: ['keyword', 'boolean', 'builtin', 'tag', 'selector', 'important'], style: {color: palette.keyword}},
      {types: ['attribute', 'annotation', 'attr-name'], style: {color: palette.attribute}},
      {types: ['class-name', 'namespace', 'property', 'variable'], style: {color: palette.type}},
      {types: ['string', 'char', 'number', 'attr-value', 'regex', 'url'], style: {color: palette.string}},
      {types: ['inserted'], style: {color: palette.inserted}},
      {types: ['deleted'], style: {color: palette.deleted}},
    ],
  };
}

export const lightCodeTheme = theme({
  background: '#f6f8f8',
  foreground: '#1f2d2d',
  keyword: '#0550ae',
  attribute: '#9a5b0b',
  type: '#127a73',
  string: '#9a5b0b',
  comment: '#6a7f7f',
  inserted: '#1a7f37',
  deleted: '#cf222e',
});

export const darkCodeTheme = theme({
  background: '#0f1a1a',
  foreground: '#cfe0e0',
  keyword: '#6fa8dc',
  attribute: '#f5b85a',
  type: '#7fd8cf',
  string: '#f5b85a',
  comment: '#6e8c8c',
  inserted: '#8ee08e',
  deleted: '#ff8a80',
});
