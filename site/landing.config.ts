import { defineLandingPage } from "subzerodev-platform-ui-landing-page";

const origin = "https://platform.subzerodev.com";
const socialImage = `${origin}/og-image.png`;
const icons = [
  { rel: "icon" as const, href: "/favicon.svg", type: "image/svg+xml" },
  {
    rel: "icon" as const,
    href: "/favicon-32x32.png",
    type: "image/png",
    sizes: "32x32",
  },
  {
    rel: "icon" as const,
    href: "/favicon-16x16.png",
    type: "image/png",
    sizes: "16x16",
  },
  { rel: "apple-touch-icon" as const, href: "/apple-touch-icon.png" },
];

export default defineLandingPage({
  allow: ["../design"],
  publicDir: "public",
  routes: [
    {
      path: "/",
      entry: "src/main.tsx",
      metadata: {
        title: "SubZeroDev.Platform — All Systems Operational",
        description:
          "SubZeroDev.Platform status: six packages, two processes, and a dependency direction enforced by the build.",
        canonicalUrl: `${origin}/`,
        openGraph: {
          title: "SubZeroDev.Platform — Status",
          description:
            "All systems operational. The layer nobody demos, monitored anyway.",
          type: "website",
          url: `${origin}/`,
          imageUrl: socialImage,
          imageWidth: 1200,
          imageHeight: 630,
        },
        twitter: { card: "summary_large_image", imageUrl: socialImage },
        icons,
        themeColor: "#f3f4f6",
        noScript: "This site needs JavaScript to render the status page.",
      },
    },
    {
      path: "/roadmap/",
      entry: "src/roadmap/main.tsx",
      metadata: {
        title: "Incident History — SubZeroDev.Platform",
        description:
          "SubZeroDev.Platform's incident history: what has shipped, what is open, and what is scheduled — derived from the slice ledger.",
        canonicalUrl: `${origin}/roadmap/`,
        openGraph: {
          title: "SubZeroDev.Platform — Incident History",
          description:
            "Every merged slice is a resolved incident. The queue is deterministic.",
          type: "website",
          url: `${origin}/roadmap/`,
          imageUrl: socialImage,
        },
        twitter: { card: "summary_large_image", imageUrl: socialImage },
        icons,
        themeColor: "#f3f4f6",
        noScript: "This site needs JavaScript to render the incident history.",
      },
    },
  ],
});
