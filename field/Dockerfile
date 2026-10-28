FROM node:22-alpine
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --omit=dev
COPY core.js items.js skillbook_prices.json quiz-progress.js server.js index.html launcher.js launcher.css privacy.html terms.html ./
COPY web ./web
USER node
ENV PORT=8080
EXPOSE 8080
CMD ["node", "server.js"]
