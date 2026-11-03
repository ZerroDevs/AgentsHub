/// <reference types="vite/client" />

/** Injected by vite.config.ts from the repo-root VERSION file. */
declare const __APP_VERSION__: string;

declare module '*.png' {
  const content: string;
  export default content;
}

declare module '*.jpg' {
  const content: string;
  export default content;
}

declare module '*.svg' {
  import React = require('react');
  export const ReactComponent: React.FC<React.SVGProps<SVGSVGElement>>;
  const content: string;
  export default content;
}
