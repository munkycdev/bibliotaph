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
RUN dotnet publish src/Bibliotaph.Server -c Release -o /app -p:UseAppHost=false \
 && install -m 755 src/Bibliotaph.Server/Container/start.sh /usr/local/bin/bibliotaph \
 && install -m 755 src/Bibliotaph.Server/Container/invite.sh /usr/local/bin/bibliotaph-invite

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-rc.2
COPY --from=build /usr/local/bin/litestream /usr/local/bin/bibliotaph /usr/local/bin/bibliotaph-invite /usr/local/bin/
COPY --from=build /app /app
COPY src/Bibliotaph.Server/Container/litestream.yml /etc/litestream.yml
RUN mkdir /data && chown app:app /data

USER app
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Server__DataRoot=/data \
    Server__LitestreamPath=/usr/local/bin/litestream
EXPOSE 8080
ENTRYPOINT ["bibliotaph"]
