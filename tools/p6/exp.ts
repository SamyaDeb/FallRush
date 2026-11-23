import { Keypair } from "@solana/web3.js";
import { bookOpen, bookClose } from "../keeper/p6.ts";
const op = process.argv[2], id = Number(process.argv[3]);
console.log(op, id, JSON.stringify(op === "open" ? await bookOpen(id, Keypair.generate().publicKey) : await bookClose(id)));
