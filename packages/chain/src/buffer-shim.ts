// Injected by esbuild: web3.js and its deps expect a global Buffer in the browser.
import { Buffer } from 'buffer';
if (!(globalThis as { Buffer?: unknown }).Buffer) (globalThis as { Buffer?: unknown }).Buffer = Buffer;
export { Buffer };
