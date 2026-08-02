import type {Config} from '@docusaurus/types';
import type * as Preset from '@docusaurus/preset-classic';

/**
 * Local Docusaurus config — overrides the base image's default when this
 * directory is copied over /template (see ./Dockerfile). Content lives in
 * ./docs; the sidebar is ./sidebar.ts.
 *
 * Broken-link checks stay 'warn', and cannot currently be 'throw'. Nothing serves
 * the site root: baseUrl is '/' while routeBasePath is 'docs', so the navbar brand
 * links to '/' from every page and each reports a broken link. Verified by building
 * with 'throw': 16 broken links, all of them '/', nothing else.
 *
 * Authoring src/pages/index.md does not fix it. docs-build.ps1 in the base image
 * strips src/pages to stop the *image* leaking its own routes, and is checked before
 * that script's overlay so a consumer's own copy is not mistaken for the leak — but
 * ./Dockerfile does `COPY . .` into /template at image-build time, so the file is
 * already there when the check runs and is deleted anyway.
 *
 * Do not re-attempt that fix, and do not migrate routeBasePath to '/' for it — that
 * moves every page URL. See agent.md, *Documentation site*, for what is still
 * unverified.
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
