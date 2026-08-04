import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import RoadmapApp from "./RoadmapApp.tsx";

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <RoadmapApp />
  </StrictMode>,
);
