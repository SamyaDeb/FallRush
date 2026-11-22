// Browser entry: esbuild bundles this into client-unity/Assets/StreamingAssets/fallrush-chain.js (IIFE).
import { createChain } from './api.ts';

(globalThis as unknown as { FallRushChain: unknown }).FallRushChain = createChain();
