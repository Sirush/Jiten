FROM node:23-alpine AS build
WORKDIR /app
COPY Jiten.Web/package*.json ./
RUN npm install
COPY Jiten.Web/ .
RUN npm run build && ls -la .output/server

FROM node:23-alpine AS final
WORKDIR /app
COPY --from=build /app/.output ./
RUN ls -la server
ENV NUXT_HOST=0.0.0.0
ENV NUXT_PORT=3001
ENV PORT=3001
EXPOSE 3001
CMD ["node", "server/index.mjs"]
