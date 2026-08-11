import js from "@eslint/js";
import tseslint from "typescript-eslint";
import react from "eslint-plugin-react";
import reactHooks from "eslint-plugin-react-hooks";

export default tseslint.config(
  {
    // tools/ runs in the Dynatrace JS runtime, not the browser, and dist/ is
    // generated — neither belongs in the UI lint program.
    ignores: ["dist/**", "node_modules/**", "tools/**"],
  },
  js.configs.recommended,
  ...tseslint.configs.recommended,
  {
    files: ["ui/**/*.{ts,tsx}"],
    plugins: { react, "react-hooks": reactHooks },
    languageOptions: {
      parserOptions: { ecmaFeatures: { jsx: true } },
    },
    settings: { react: { version: "detect" } },
    rules: {
      ...reactHooks.configs.recommended.rules,
      // The Sankey layout mutates d3's own node/link objects, so the non-null
      // assertions on x0/y0/index are load-bearing rather than sloppy.
      "@typescript-eslint/no-non-null-assertion": "off",
    },
  }
);
