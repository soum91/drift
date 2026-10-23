package dev.soum91.drift;

import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.security.*;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicBoolean;

/** Small, bounded HTTP receiver. Bound only to the Wi-Fi Direct owner's address. */
public final class TransferServer implements Closeable {
    public interface Events {
        boolean approve(String name, long size) throws InterruptedException;
        void progress(String name, long done, long total);
        void received(File file, String sha256);
        void error(String message);
    }
    public static final long MAX_FILE = 8L * 1024 * 1024 * 1024;
    private final File folder;
    private final byte[] page;
    private final Events events;
    private final String token = randomToken();
    private final long expiresAt = System.nanoTime() + TimeUnit.MINUTES.toNanos(15);
    private final AtomicBoolean busy = new AtomicBoolean();
    private final Set<Socket> sockets = ConcurrentHashMap.newKeySet();
    private final ThreadPoolExecutor workers = new ThreadPoolExecutor(3, 3, 0,
        TimeUnit.SECONDS, new ArrayBlockingQueue<>(3));
    private volatile ServerSocket listener;
    private volatile boolean closed;
    private Ticket pending;
    private volatile long cancellations;
    private volatile Socket activeUpload;
    private static final class Ticket {
        final String id = randomToken(), name;
        final long size, expires = System.nanoTime() + TimeUnit.MINUTES.toNanos(2);
        Ticket(String name, long size) { this.name = name; this.size = size; }
    }
    public TransferServer(File folder, byte[] page, Events events) throws IOException {
        this.folder = folder; this.page = page; this.events = events;
        if (!folder.isDirectory() && !folder.mkdirs()) throw new IOException("Cannot create received folder");
    }
    public String token() { return token; }
    public int start(InetAddress address, int port) throws IOException {
        listener = new ServerSocket();
        listener.bind(new InetSocketAddress(address, port), 4);
        Thread acceptor = new Thread(() -> {
            while (!closed) {
                try {
                    Socket client = listener.accept();
                    client.setSoTimeout(90000);
                    sockets.add(client);
                    try { workers.execute(() -> serve(client)); }
                    catch (RejectedExecutionException e) { sockets.remove(client); client.close(); }
                } catch (IOException e) { if (!closed) events.error("Receiver socket stopped"); }
            }
        }, "drift-listener");
        acceptor.setDaemon(true); acceptor.start();
        return listener.getLocalPort();
    }
    public static String randomToken() {
        byte[] bytes = new byte[24]; new SecureRandom().nextBytes(bytes);
        return Base64.getUrlEncoder().withoutPadding().encodeToString(bytes);
    }
    public static String safeName(String name) throws IOException {
        if (name == null || name.length() == 0 || name.length() > 180 || name.equals(".") || name.equals(".."))
            throw new IOException("Invalid filename");
        for (char c : name.toCharArray())
            if (c < 32 || c == 127 || c == '/' || c == '\\') throw new IOException("Invalid filename");
        if (name.startsWith(".")) throw new IOException("Hidden filenames are not accepted");
        return name;
    }
    private void serve(Socket client) {
        try (Socket ignored = client) {
            InputStream input = new BufferedInputStream(client.getInputStream());
            OutputStream output = client.getOutputStream();
            String header = readHeader(input);
            String[] lines = header.split("\r\n");
            String[] request = lines[0].split(" ");
            if (request.length != 3) { reply(output, 400, "Bad request"); return; }
            Map<String,String> headers = new HashMap<>();
            for (int i = 1; i < lines.length; i++) {
                int colon = lines[i].indexOf(':');
                if (colon < 1) { reply(output, 400, "Bad header"); return; }
                String key = lines[i].substring(0, colon).trim().toLowerCase(Locale.ROOT);
                if (headers.put(key, lines[i].substring(colon + 1).trim()) != null) {
                    reply(output, 400, "Duplicate header"); return;
                }
            }
            if (headers.containsKey("transfer-encoding")) { reply(output, 400, "Content-Length required"); return; }
            URI uri;
            try { uri = new URI(request[1]); } catch (URISyntaxException e) { reply(output, 400, "Bad URL"); return; }
            if ("GET".equals(request[0]) && "/".equals(uri.getPath())) {
                respond(output, 200, "text/html; charset=utf-8", page); return;
            }
            if (!"POST".equals(request[0])) { reply(output, 404, "Not found"); return; }
            String authorization = headers.getOrDefault("authorization", "");
            if (closed || System.nanoTime() > expiresAt || !MessageDigest.isEqual(
                    authorization.getBytes(StandardCharsets.UTF_8), ("Bearer " + token).getBytes(StandardCharsets.UTF_8))) {
                reply(output, 403, "Pairing expired or invalid. On TV choose Stop, then Start, and scan the new codes."); return;
            }
            // Disallow browser cross-origin submissions. The token is carried only by this page.
            String origin = headers.get("origin"), host = headers.get("host");
            if (origin != null && !origin.equals("http://" + host)) { reply(output, 403, "Wrong origin"); return; }
            Map<String,String> query = query(uri.getRawQuery());
            if ("/cancel".equals(uri.getPath())) {
                synchronized (this) { cancellations++; pending = null; }
                Socket uploading = activeUpload;
                if (uploading != null) try { uploading.close(); } catch (IOException closeError) {}
                reply(output, 200, "Cancelled"); return;
            }
            if ("/offer".equals(uri.getPath())) {
                long size;
                String name;
                try { name = safeName(query.get("name")); size = Long.parseLong(query.getOrDefault("size", "-1")); }
                catch (Exception e) { reply(output, 400, "Invalid file details"); return; }
                if (size < 0 || size > MAX_FILE || size > folder.getUsableSpace() - 16 * 1024 * 1024) {
                    reply(output, 413, "File exceeds available storage or the 8 GiB limit"); return;
                }
                if (!busy.compareAndSet(false, true)) { reply(output, 409, "Another transfer is active"); return; }
                try {
                    long epoch = cancellations;
                    synchronized (this) {
                        if (pending != null && pending.expires > System.nanoTime()) {
                            reply(output, 409, "A file is already approved. Send it or wait two minutes."); return;
                        }
                    }
                    if (!events.approve(name, size)) { reply(output, 403, "Declined on TV"); return; }
                    synchronized (this) {
                        if (epoch != cancellations || closed) { reply(output, 409, "Cancelled"); return; }
                        pending = new Ticket(name, size); reply(output, 200, pending.id);
                    }
                } finally { busy.set(false); }
                return;
            }
            if ("/upload".equals(uri.getPath())) {
                if (!busy.compareAndSet(false, true)) { reply(output, 409, "Another transfer is active"); return; }
                File partial = null;
                try {
                    Ticket ticket;
                    long epoch;
                    synchronized (this) {
                        epoch = cancellations;
                        ticket = pending;
                        if (ticket == null || ticket.expires < System.nanoTime() || !ticket.id.equals(query.get("ticket"))) {
                            reply(output, 403, "Approval expired. Send the file again."); return;
                        }
                        pending = null; // One-use approval, consumed even on interrupted uploads.
                    }
                    long length;
                    try { length = Long.parseLong(headers.getOrDefault("content-length", "-1")); }
                    catch (NumberFormatException e) { reply(output, 400, "Invalid length"); return; }
                    if (length != ticket.size) { reply(output, 400, "File size changed"); return; }
                    activeUpload = client;
                    partial = File.createTempFile(".drift-", ".part", folder);
                    MessageDigest digest = MessageDigest.getInstance("SHA-256");
                    long done = 0, last = 0;
                    try (FileOutputStream file = new FileOutputStream(partial)) {
                        byte[] buffer = new byte[128 * 1024];
                        while (done < length) {
                            if (closed || epoch != cancellations) throw new IOException("Stopped");
                            int count = input.read(buffer, 0, (int)Math.min(buffer.length, length - done));
                            if (count < 0) throw new EOFException("Transfer interrupted");
                            file.write(buffer, 0, count); digest.update(buffer, 0, count); done += count;
                            long now = System.nanoTime();
                            if (now - last > 250000000L || done == length) {
                                events.progress(ticket.name, done, length); last = now;
                            }
                        }
                        file.getFD().sync();
                    }
                    if (closed || epoch != cancellations) throw new IOException("Stopped");
                    File destination = uniqueDestination(ticket.name);
                    Files.move(partial.toPath(), destination.toPath()); partial = null;
                    StringBuilder sha = new StringBuilder();
                    for (byte b : digest.digest()) sha.append(String.format(Locale.ROOT, "%02x", b & 255));
                    events.received(destination, sha.toString());
                    reply(output, 200, "Saved: " + destination.getName() + "\nSHA-256: " + sha);
                } finally {
                    activeUpload = null;
                    if (partial != null) partial.delete();
                    busy.set(false);
                }
                return;
            }
            reply(output, 404, "Not found");
        } catch (Exception e) {
            if (!closed) events.error("Transfer failed: " + e.getClass().getSimpleName());
        } finally { sockets.remove(client); }
    }
    private File uniqueDestination(String name) throws IOException {
        File candidate = new File(folder, name);
        int dot = name.lastIndexOf('.');
        String stem = dot > 0 ? name.substring(0, dot) : name;
        String ext = dot > 0 ? name.substring(dot) : "";
        for (int i = 1; candidate.exists(); i++) {
            if (i > 9999) throw new IOException("Too many duplicate files");
            candidate = new File(folder, stem + " (" + i + ")" + ext);
        }
        return candidate;
    }
    private static Map<String,String> query(String raw) throws IOException {
        Map<String,String> result = new HashMap<>();
        if (raw != null) for (String item : raw.split("&")) {
            String[] pair = item.split("=", 2);
            result.put(URLDecoder.decode(pair[0], "UTF-8"), pair.length == 2 ? URLDecoder.decode(pair[1], "UTF-8") : "");
        }
        return result;
    }
    private static String readHeader(InputStream input) throws IOException {
        ByteArrayOutputStream bytes = new ByteArrayOutputStream();
        int state = 0;
        while (bytes.size() < 16384) {
            int b = input.read(); if (b < 0) throw new EOFException(); bytes.write(b);
            if ((state == 0 || state == 2) && b == '\r') state++;
            else if ((state == 1 || state == 3) && b == '\n') { if (++state == 4) return bytes.toString("US-ASCII"); }
            else state = b == '\r' ? 1 : 0;
        }
        throw new IOException("Headers too large");
    }
    private static void reply(OutputStream out, int code, String message) throws IOException {
        respond(out, code, "text/plain; charset=utf-8", message.getBytes(StandardCharsets.UTF_8));
    }
    private static void respond(OutputStream out, int code, String type, byte[] bytes) throws IOException {
        String headers = "HTTP/1.1 " + code + " Result\r\nContent-Type: " + type + "\r\nContent-Length: " + bytes.length
            + "\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff"
            + "\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'\r\n\r\n";
        out.write(headers.getBytes(StandardCharsets.US_ASCII)); out.write(bytes); out.flush();
    }
    @Override public void close() {
        closed = true;
        try { if (listener != null) listener.close(); } catch (IOException ignored) {}
        for (Socket socket : sockets) try { socket.close(); } catch (IOException ignored) {}
        workers.shutdownNow();
        synchronized (this) { pending = null; }
    }
}
