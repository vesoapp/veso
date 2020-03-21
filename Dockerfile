ARG DOTNET_VERSION=2.2
ARG FFMPEG_VERSION=latest

FROM node:alpine as web-builder
ARG JELLYFIN_WEB_VERSION=master
RUN apk add curl \
 && curl -L https://github.com/vesotv/veso-web/archive/${JELLYFIN_WEB_VERSION}.tar.gz | tar zxf - \
 && cd veso-web-* \
 && yarn install \
 && yarn build \
 && mv dist /dist

FROM mcr.microsoft.com/dotnet/core/sdk:${DOTNET_VERSION} as builder
WORKDIR /repo
COPY . .
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
RUN dotnet publish Veso.Server --configuration Release --output="/veso" --self-contained --runtime linux-x64 "-p:GenerateDocumentationFile=false;DebugSymbols=false;DebugType=none"

FROM jellyfin/ffmpeg:${FFMPEG_VERSION} as ffmpeg

FROM mcr.microsoft.com/dotnet/core/runtime:${DOTNET_VERSION}
COPY --from=ffmpeg / /
COPY --from=builder /veso /veso
COPY --from=web-builder /dist /veso/veso-web
# Install dependencies:
#   libfontconfig1: needed for Skia
#   mesa-va-drivers: needed for VAAPI
RUN apt-get update \
 && apt-get install --no-install-recommends --no-install-suggests -y \
   libfontconfig1 mesa-va-drivers \
 && apt-get clean autoclean \
 && apt-get autoremove \
 && rm -rf /var/lib/apt/lists/* \
 && mkdir -p /cache /config /media \
 && chmod 777 /cache /config /media

EXPOSE 8096
VOLUME /cache /config /media
ENTRYPOINT dotnet /veso/veso.dll \
    --datadir /config \
    --cachedir /cache \
    --ffmpeg /usr/local/bin/ffmpeg
