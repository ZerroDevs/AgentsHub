// The repo-root VERSION file holds a bare version number, not TypeScript. Bun reads it with the
// text loader (see sidecar/appVersion.ts); this only teaches the type checker about that import.
declare module '*VERSION' {
  const text: string;
  export default text;
}
