# The web server's image. Build it from the repository root:
#   docker build -f src/Bibliotaph.Server/Container/Dockerfile -t bibliotaph-server .
# It sticks to what the classic builder understands, so az acr build can build it in Azure without Docker installed.

FROM mcr.microsoft.com/dotnet/sdk:11.0.100-rc.2 AS build

# Litestream (Apache-2.0) from its GitHub release rather than its Docker Hub image, so a build never meets Hub's
# pull limits; the checksum pins the exact file.
ARG LITESTREAM_VERSION=0.5.17
ARG LITESTREAM_SHA256=cfb371176d164437ae869f8351cfde49bd1804ae71c61923f75c9cba9c9c006d
RUN curl -fsSL -o /tmp/litestream.tar.gz \
      "https://github.com/benbjohnson/litestream/releases/download/v${LITESTREAM_VERSION}/litestream-${LITESTREAM_VERSION}-linux-x86_64.tar.gz" \
 && echo "${LITESTREAM_SHA256}  /tmp/litestream.tar.gz" | sha256sum -c - \
 && tar -xzf /tmp/litestream.tar.gz -C /usr/local/bin litestream

WORKDIR /src
COPY . .
# The PDF worker is its own process, as in the Windows app, so a book that crashes PDFium can't take the server down.
RUN dotnet publish src/Bibliotaph.Server -c Release -r linux-x64 --self-contained false -o /app -p:UseAppHost=false \
 && dotnet publish src/Bibliotaph.PdfWorker -c Release -r linux-x64 --self-contained false -o /app/pdfworker -p:PdfWorkerPortable=true \
 && install -m 755 src/Bibliotaph.Server/Container/start.sh /usr/local/bin/bibliotaph \
 && install -m 755 src/Bibliotaph.Server/Container/invite.sh /usr/local/bin/bibliotaph-invite

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-rc.2
# Tesseract (Apache-2.0) and its English data, which the PDF worker loads to read scanned pages. One thread a page, so
# OCR never takes the whole container from the people using it.
RUN apt-get update \
 && apt-get install -y --no-install-recommends tesseract-ocr tesseract-ocr-eng \
 && rm -rf /var/lib/apt/lists/*
COPY --from=build /usr/local/bin/litestream /usr/local/bin/bibliotaph /usr/local/bin/bibliotaph-invite /usr/local/bin/
COPY --from=build /app /app
COPY src/Bibliotaph.Server/Container/litestream.yml /etc/litestream.yml
RUN mkdir /data && chown app:app /data

USER app
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    OMP_THREAD_LIMIT=1 \
    Server__DataRoot=/data \
    Server__LitestreamPath=/usr/local/bin/litestream
EXPOSE 8080
ENTRYPOINT ["bibliotaph"]
