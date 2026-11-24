# Built from the package folder (build.bat server -> build/kns-server), which holds server/ (self-contained linux-x64) and web/.
#   docker compose up -d --build
FROM mcr.microsoft.com/dotnet/runtime-deps:8.0
RUN useradd --system --home-dir /var/lib/kns --shell /usr/sbin/nologin kns \
    && mkdir -p /var/lib/kns && chown kns:kns /var/lib/kns
COPY server /opt/kns/server
COPY web /opt/kns/web
ENV KNS_PORT=8092 \
    KNS_BIND=0.0.0.0 \
    KNS_PUBLIC_BASE=/ \
    KNS_WEB_ROOT=/opt/kns/web \
    KNS_DB=/var/lib/kns/kns.sqlite \
    KNS_QUIET=1 \
    KNS_LOG_DIR=/var/lib/kns/logs \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
USER kns
WORKDIR /var/lib/kns
VOLUME /var/lib/kns
EXPOSE 8092
HEALTHCHECK --interval=30s --timeout=3s CMD ["/bin/bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8092 && printf 'GET /health HTTP/1.0\\r\\n\\r\\n' >&3 && grep -q ' 200 ' <&3"]
ENTRYPOINT ["/opt/kns/server/KnS.Server"]
