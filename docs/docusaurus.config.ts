import type {Config} from '@docusaurus/types';
import type * as Preset from '@docusaurus/preset-classic';

/**
 * Local Docusaurus config — overrides the base image's default when this
 * directory is copied over /template (see ./Dockerfile). Content lives in
 * ./docs; the sidebar is ./sidebar.ts.
 *
 * Broken-link checks are 'throw'. They were 'warn' while nothing served the site
 * root — baseUrl '/' with routeBasePath 'docs' left the navbar brand linking to '/'
 * from every page, 16 broken links, all of them '/'. Invoke-SetupDocs generates
 * src/pages/index.md from README.md and fixes it; the sanctioned build now reports
 * zero broken links, so the check can gate.
 *
 * Build it the way CI does — `Invoke-DocsBuild -SourceDocs ./docs` against the clean
 * base image, repository mounted. Do NOT `docker build` this directory and then run
 * the build inside the derived image: ./Dockerfile does `COPY . .` into /template, so
 * src/pages is already populated when docs-build.ps1 checks it for a base-image leak,
 * and the generated site root is deleted as that leak. That diagnosis cost two wrong
 * conclusions; see agent.md, *Documentation site*.
 */
const config: Config = {
  title: 'SubZeroDev.Platform',
  tagline: 'The reusable application framework and hosting layer',
  url: 'https://platform.subzerodev.com',
  baseUrl: '/',

  onBrokenLinks: 'throw',

  markdown: {
    hooks: {
      onBrokenMarkdownLinks: 'throw',
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
