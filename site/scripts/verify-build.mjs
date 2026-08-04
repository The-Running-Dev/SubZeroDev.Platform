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

const requiredTags = [
  /<title>SubZeroDev\.Platform — All Systems Operational<\/title>/,
  /<meta\s+property="og:title"\s+content="SubZeroDev\.Platform — Status"\s*\/>/,
  /<meta\s+property="og:type"\s+content="website"\s*\/>/,
  /<meta\s+property="og:url"\s+content="https:\/\/platform\.subzerodev\.com\/"\s*\/>/,
  /<meta\s+property="og:image"\s+content="https:\/\/platform\.subzerodev\.com\/og-image\.png"\s*\/>/,
  /<link\s+rel="canonical"\s+href="https:\/\/platform\.subzerodev\.com\/"\s*\/>/,
  /<meta\s+name="twitter:card"\s+content="summary_large_image"\s*\/>/,
  /<link\s+rel="icon"\s+type="image\/svg\+xml"\s+href="\/favicon\.svg"\s*\/>/,
];

for (const tag of requiredTags) {
  if (!tag.test(landingHtml)) {
    throw new Error(
      `Built HTML is missing required static metadata: ${tag.source}`,
    );
  }
}

const roadmapTags = [
  /<title>Incident History — SubZeroDev\.Platform<\/title>/,
  /<meta\s+property="og:url"\s+content="https:\/\/platform\.subzerodev\.com\/roadmap\/"\s*\/>/,
  /<link\s+rel="canonical"\s+href="https:\/\/platform\.subzerodev\.com\/roadmap\/"\s*\/>/,
  /<script type="module" crossorigin src="\/assets\//,
];

for (const tag of roadmapTags) {
  if (!tag.test(roadmapHtml)) {
    throw new Error(
      `Built roadmap HTML is missing required metadata: ${tag.source}`,
    );
  }
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
