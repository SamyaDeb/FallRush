// Real web3.js Connection factory (kept separate so tests never touch the network).
import { Connection } from '@solana/web3.js';
import type { ConnFactory, ConnLike } from './rollup.ts';

export const web3ConnFactory: ConnFactory = (http, ws, commitment) =>
  new Connection(http, { commitment: commitment as 'processed', wsEndpoint: ws ?? undefined, disableRetryOnRateLimit: true }) as unknown as ConnLike;
