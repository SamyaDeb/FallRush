# fallrush-node: keeper daemon + indexer (Node 24, TypeScript via --experimental-strip-types). Build context = a
# staging dir made by scripts/p5-deploy.sh (explicit files only: no keys, no .env).
FROM node:24-slim
WORKDIR /app
COPY package.json ./
RUN npm install --omit=dev --no-audit --no-fund && npm cache clean --force
COPY tools ./tools
COPY programs/target/idl ./programs/target/idl
ENV NODE_ENV=production
