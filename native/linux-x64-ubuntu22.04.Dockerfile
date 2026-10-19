# Ubuntu 22.04 (glibc 2.35) is the oldest supported Linux x64 release.
FROM ubuntu:22.04@sha256:829f6df217bcbae2b371026e81711d1a787c61b2967ad09d015063663ebafbf7

ENV DEBIAN_FRONTEND=noninteractive
ENV DOTNET_ROOT=/usr/share/dotnet
ENV PATH=/usr/share/dotnet:$PATH

ARG UBUNTU_SNAPSHOT=20250901T000000Z
ARG DOTNET_SDK_VERSION=10.0.400
ARG DOTNET_SDK_SHA512=1033977dd837150e0814cf0c5d5b17ceb63925fda7ba2158b47258a4bd7c048cf82eac3bc1166f3146f53124a3f5fba09db1de1260d2ce96399860303b404b48

RUN sed -i \
        -e "s|http://archive.ubuntu.com/ubuntu/|https://snapshot.ubuntu.com/ubuntu/${UBUNTU_SNAPSHOT}/|g" \
        -e "s|http://security.ubuntu.com/ubuntu/|https://snapshot.ubuntu.com/ubuntu/${UBUNTU_SNAPSHOT}/|g" \
        /etc/apt/sources.list \
    && apt-get update -o Acquire::Check-Valid-Until=false \
    && apt-get install -y --no-install-recommends \
        autoconf \
        automake \
        build-essential \
        ca-certificates \
        clang \
        cmake \
        curl \
        diffutils \
        file \
        git \
        gperf \
        gzip \
        libicu70 \
        libegl-dev \
        libgl-dev \
        libssl3 \
        libtool \
        libvulkan-dev \
        make \
        nasm \
        ninja-build \
        patch \
        perl \
        pkg-config \
        python3 \
        python3-jinja2 \
        python3-setuptools \
        unzip \
        xz-utils \
        zlib1g \
    && curl -fsSL \
        "https://builds.dotnet.microsoft.com/dotnet/Sdk/${DOTNET_SDK_VERSION}/dotnet-sdk-${DOTNET_SDK_VERSION}-linux-x64.tar.gz" \
        -o /tmp/dotnet-sdk.tar.gz \
    && printf '%s  %s\n' "$DOTNET_SDK_SHA512" /tmp/dotnet-sdk.tar.gz | sha512sum -c - \
    && mkdir -p "$DOTNET_ROOT" \
    && tar -xzf /tmp/dotnet-sdk.tar.gz -C "$DOTNET_ROOT" \
    && rm /tmp/dotnet-sdk.tar.gz \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /workspace
