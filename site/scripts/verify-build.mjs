import { access, readFile } from "node:fs/promises";

const landingHtml = await readFile(
  new URL("../dist/index.html", import.meta.url),
  "utf8",
);
const roadmapHtml = await readFile(
  new URL("../dist/roadmap/index.html", import.meta.url),
  "utf8",
);

const requiredAssets = [
  "og-image.png",
  "favicon.svg",
  "favicon-16x16.png",
  "favicon-32x32.png",
  "apple-touch-icon.png",
];

for (const asset of requiredAssets) {
  await access(new URL(`../dist/${asset}`, import.meta.url)).catch(() => {
    throw new Error(`Built output is missing referenced asset: ${asset}`);
  });
}

function requireTag(html, label, attributes) {
  const escapeAttribute = (value) =>
    value.replace(
      /[&<>"']/g,
      (character) =>
        ({
          "&": "&amp;",
          "<": "&lt;",
          ">": "&gt;",
          '"': "&quot;",
          "'": "&#39;",
        })[character],
    );
  const found = [...html.matchAll(/<[^>]+>/g)].some(([tag]) =>
    attributes.every(([name, value]) =>
      tag.includes(`${name}="${escapeAttribute(value)}"`),
    ),
  );
  if (!found) {
    throw new Error(`Built HTML is missing required static metadata: ${label}`);
  }
}

function requireStaticHead(html, route) {
  if (!html.includes(`<title>${route.title}</title>`)) {
    throw new Error(`Built HTML is missing ${route.name} title.`);
  }
  requireTag(html, `${route.name} description`, [
    ["name", "description"],
    ["content", route.description],
  ]);
  requireTag(html, `${route.name} canonical URL`, [
    ["rel", "canonical"],
    ["href", route.url],
  ]);
  requireTag(html, `${route.name} Open Graph title`, [
    ["property", "og:title"],
    ["content", route.openGraphTitle],
  ]);
  requireTag(html, `${route.name} Open Graph description`, [
    ["property", "og:description"],
    ["content", route.openGraphDescription],
  ]);
  for (const [property, content] of [
    ["og:type", "website"],
    ["og:url", route.url],
    ["og:image", "https://platform.subzerodev.com/og-image.png"],
  ]) {
    requireTag(html, `${route.name} ${property}`, [
      ["property", property],
      ["content", content],
    ]);
  }
  requireTag(html, `${route.name} X card`, [
    ["name", "twitter:card"],
    ["content", "summary_large_image"],
  ]);
  requireTag(html, `${route.name} X image`, [
    ["name", "twitter:image"],
    ["content", "https://platform.subzerodev.com/og-image.png"],
  ]);
  requireTag(html, `${route.name} theme colour`, [
    ["name", "theme-color"],
    ["content", "#f3f4f6"],
  ]);
  for (const [href, type, sizes] of [
    ["/favicon.svg", "image/svg+xml", undefined],
    ["/favicon-32x32.png", "image/png", "32x32"],
    ["/favicon-16x16.png", "image/png", "16x16"],
  ]) {
    requireTag(html, `${route.name} icon ${href}`, [
      ["rel", "icon"],
      ["href", href],
      ["type", type],
      ...(sizes ? [["sizes", sizes]] : []),
    ]);
  }
  requireTag(html, `${route.name} Apple touch icon`, [
    ["rel", "apple-touch-icon"],
    ["href", "/apple-touch-icon.png"],
  ]);
  if (!html.includes(`<noscript>${route.noScript}</noscript>`)) {
    throw new Error(`Built HTML is missing ${route.name} no-script text.`);
  }
}

requireStaticHead(landingHtml, {
  name: "landing",
  title: "SubZeroDev.Platform — All Systems Operational",
  description:
    "SubZeroDev.Platform status: six packages, two processes, and a dependency direction enforced by the build.",
  url: "https://platform.subzerodev.com/",
  openGraphTitle: "SubZeroDev.Platform — Status",
  openGraphDescription:
    "All systems operational. The layer nobody demos, monitored anyway.",
  noScript: "This site needs JavaScript to render the status page.",
});

requireTag(landingHtml, "landing Open Graph image width", [
  ["property", "og:image:width"],
  ["content", "1200"],
]);
requireTag(landingHtml, "landing Open Graph image height", [
  ["property", "og:image:height"],
  ["content", "630"],
]);

requireStaticHead(roadmapHtml, {
  name: "roadmap",
  title: "Incident History — SubZeroDev.Platform",
  description:
    "SubZeroDev.Platform's incident history: what has shipped, what is open, and what is scheduled — derived from the slice ledger.",
  url: "https://platform.subzerodev.com/roadmap/",
  openGraphTitle: "SubZeroDev.Platform — Incident History",
  openGraphDescription:
    "Every merged slice is a resolved incident. The queue is deterministic.",
  noScript: "This site needs JavaScript to render the incident history.",
});

if (
  !/<script\s+type="module"\s+crossorigin\s+src="\/assets\//.test(roadmapHtml)
) {
  throw new Error("Built roadmap HTML is missing its module entry.");
}

if (landingHtml === roadmapHtml) {
  throw new Error(
    "Landing and roadmap built to identical HTML — the two entry points did not produce distinct pages.",
  );
}

if (/\/src\//.test(landingHtml) || /\/src\//.test(roadmapHtml)) {
  throw new Error("Built HTML references a development-only source path.");
}

console.log(
  "Both built HTML entry points contain their required static metadata.",
);
