# Static Unity WebGL build with pre-compressed Brotli files (Unity: Compression Format = Brotli,
# Decompression Fallback = off). The official nginx image has no brotli module; it only needs to send
# the right headers for the .br files Unity already produced.
root /usr/share/nginx/html;
index index.html;

location ~ .+\.(data|symbols\.json)\.br$ {
    gzip off;
    add_header Content-Encoding br;
    default_type application/octet-stream;
}
location ~ .+\.js\.br$ {
    gzip off;
    add_header Content-Encoding br;
    default_type application/javascript;
}
location ~ .+\.wasm\.br$ {
    gzip off;
    add_header Content-Encoding br;
    default_type application/wasm;
}
