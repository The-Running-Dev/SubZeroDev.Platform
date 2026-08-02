import type {Config} from '@docusaurus/types';
import type * as Preset from '@docusaurus/preset-classic';

/**
 * Local Docusaurus config — overrides the base image's default when this
 * directory is copied over /template (see ./Dockerfile). Content lives in
 * ./docs; the sidebar is ./sidebar.ts.
 *
 * Broken-link checks stay 'warn', and cannot currently be 'throw'. Nothing serves
 * the site root: baseUrl is '/' while routeBasePath is 'docs', so the navbar brand
 * links to '/' from every page and each reports a broken link. Authoring
 * src/pages/index.md does not fix it — the base image's pre-build step strips
 * everything under src/pages, treating it as base-image content this project did
 * not author. Verified by building with 'throw': the only broken link is '/'.
 *
 * Fixing it needs either a docs-template change, or routeBasePath: '/' — an
 * information-architecture change that moves every page's URL, so not taken here.
 */
const config: Config = {
  title: 'SubZeroDev.Platform',
  tagline: 'The reusable application framework and hosting layer',
  url: 'https://platform.subzerodev.com',
  baseUrl: '/',

  onBrokenLinks: 'warn',

  markdown: {
    hooks: {
      onBrokenMarkdownLinks: 'warn',
    },
  },

  i18n: {defaultLocale: 'en', locales: ['en']},

  presets: [
    [
      'classic',
      {
        docs: {
          sidebarPath: './sidebar.ts',
          routeBasePath: 'docs',
        },
        blog: false,
      } satisfies Preset.Options,
    ],
  ],

  themeConfig: {
    navbar: {
      title: 'SubZeroDev.Platform',
      items: [
        {type: 'docSidebar', sidebarId: 'docs', position: 'left', label: 'Docs'},
      ],
    },
    footer: {style: 'dark', links: []},
  } satisfies Preset.ThemeConfig,
};

export default config;
