package dev.soum91.drift;

import android.Manifest;
import android.app.*;
import android.content.*;
import android.content.pm.PackageManager;
import android.graphics.*;
import android.graphics.drawable.GradientDrawable;
import android.location.LocationManager;
import android.net.Uri;
import android.net.wifi.WifiManager;
import android.net.wifi.p2p.*;
import android.os.*;
import android.provider.Settings;
import android.view.*;
import android.widget.*;
import com.google.zxing.*;
import com.google.zxing.common.BitMatrix;
import com.google.zxing.qrcode.QRCodeWriter;
import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicBoolean;

public final class MainActivity extends Activity {
    private final Handler main = new Handler(Looper.getMainLooper());
    private final StringBuilder log = new StringBuilder();
    private WifiP2pManager manager;
    private WifiP2pManager.Channel channel;
    private TransferServer server;
    private boolean registered, starting, ownedGroup, destroyed;
    private volatile int generation;
    private TextView state, detail, progress, networkName;
    private ImageView wifiQr, sendQr;
    private Button start, stop;
    private AlertDialog consent;
    private File pendingInstall;
    private final BroadcastReceiver receiver = new BroadcastReceiver() {
        @Override public void onReceive(Context context, Intent intent) {
            if (WifiP2pManager.WIFI_P2P_STATE_CHANGED_ACTION.equals(intent.getAction())) {
                int value = intent.getIntExtra(WifiP2pManager.EXTRA_WIFI_STATE, -1);
                record("P2P state: " + value);
                if (value == WifiP2pManager.WIFI_P2P_STATE_DISABLED && (starting || server != null)) {
                    stopSession(); message("Wi-Fi Direct unavailable", "Enable Wi-Fi, then try Start direct transfer again.");
                }
            } else if (starting || server != null) refreshGroup(generation, 0);
        }
    };
    @Override public void onCreate(Bundle saved) {
        super.onCreate(saved);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        buildUi();
        record(Build.MODEL + " / Android " + Build.VERSION.RELEASE + " / API " + Build.VERSION.SDK_INT);
        record("ABIs: " + Arrays.toString(Build.SUPPORTED_ABIS));
        record("Wi-Fi Direct feature: " + getPackageManager().hasSystemFeature(PackageManager.FEATURE_WIFI_DIRECT));
        record("Wi-Fi Aware feature: " + getPackageManager().hasSystemFeature("android.hardware.wifi.aware"));
        manager = (WifiP2pManager)getSystemService(WIFI_P2P_SERVICE);
        try {
            if (manager != null) channel = manager.initialize(this, getMainLooper(), () -> {
                stopSession(); message("Wi-Fi service disconnected", "Restart Drift and try again.");
            });
        } catch (RuntimeException e) { record("P2P initialization: " + e.getClass().getSimpleName()); }
        record("P2P manager: " + (manager != null) + "; channel: " + (channel != null));
        IntentFilter filter = new IntentFilter();
        filter.addAction(WifiP2pManager.WIFI_P2P_STATE_CHANGED_ACTION);
        filter.addAction(WifiP2pManager.WIFI_P2P_CONNECTION_CHANGED_ACTION);
        if (Build.VERSION.SDK_INT >= 33) registerReceiver(receiver, filter, Context.RECEIVER_EXPORTED);
        else registerReceiver(receiver, filter);
        registered = true;
    }
    private void buildUi() {
        LinearLayout root = new LinearLayout(this); root.setOrientation(LinearLayout.HORIZONTAL);
        root.setPadding(dp(36), dp(24), dp(36), dp(24)); root.setBackgroundColor(Color.rgb(16,24,39));
        LinearLayout left = new LinearLayout(this); left.setOrientation(LinearLayout.VERTICAL);
        root.addView(left, new LinearLayout.LayoutParams(0, -1, 1.1f));
        TextView brand = label("DRIFT  /  DIRECT TRANSFER", 15, 0xff6fe4bd); left.addView(brand);
        state = label("Bring your files closer.", 30, Color.WHITE); left.addView(state);
        detail = label("Create a private link between your iPhone and TV.\nNo router, account, or IP entry.", 17, 0xffb2bfd3); left.addView(detail);
        start = button("Start direct transfer", () -> begin()); left.addView(start);
        stop = button("Stop and disconnect", () -> stopSession()); stop.setEnabled(false); left.addView(stop);
        LinearLayout tools = new LinearLayout(this);
        tools.addView(button("Received files", () -> showFiles()));
        tools.addView(button("Diagnostics", () -> diagnostics())); left.addView(tools);
        long build = 1;
        try { build = getPackageManager().getPackageInfo(getPackageName(),0).getLongVersionCode(); } catch (PackageManager.NameNotFoundException ignored) {}
        progress = label("Prototype 0.1.0 / build " + build + "\nKeep Drift open while receiving.", 14, 0xff8d9bb2); left.addView(progress);
        LinearLayout right = new LinearLayout(this); right.setOrientation(LinearLayout.VERTICAL); right.setGravity(Gravity.CENTER);
        root.addView(right, new LinearLayout.LayoutParams(0, -1, 1));
        networkName = label("Your direct link will appear here", 16, 0xffb2bfd3); networkName.setGravity(Gravity.CENTER); right.addView(networkName);
        LinearLayout cards = new LinearLayout(this); cards.setGravity(Gravity.CENTER); right.addView(cards);
        wifiQr = qrCard(cards, "1 / Join TV Wi-Fi"); sendQr = qrCard(cards, "2 / Send files");
        TextView hint = label("Scan with iPhone Camera. After joining,\nscan the second code to open Safari.\nYour phone may report 'No Internet'.", 14, 0xff8d9bb2); hint.setGravity(Gravity.CENTER); right.addView(hint);
        setContentView(root); start.requestFocus();
    }
    private TextView label(String text, int size, int color) {
        TextView t = new TextView(this); t.setText(text); t.setTextSize(size); t.setTextColor(color);
        t.setPadding(0, dp(8), dp(12), dp(8)); return t;
    }
    private Button button(String text, Runnable action) {
        Button b = new Button(this); b.setText(text); b.setTextSize(14); b.setAllCaps(false); b.setMinHeight(dp(44));
        b.setPadding(dp(16), dp(5), dp(16), dp(5));
        b.setOnFocusChangeListener((v, focused) -> {
            GradientDrawable bg = new GradientDrawable(); bg.setCornerRadius(dp(10));
            bg.setColor(focused ? 0xff6fe4bd : 0xff24354e); b.setBackground(bg); b.setTextColor(focused ? 0xff101827 : Color.WHITE);
        });
        GradientDrawable bg = new GradientDrawable(); bg.setCornerRadius(dp(10)); bg.setColor(0xff24354e); b.setBackground(bg); b.setTextColor(Color.WHITE);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(-2, dp(46)); lp.setMargins(0, dp(5), dp(10), dp(5)); b.setLayoutParams(lp);
        b.setOnClickListener(v -> action.run()); return b;
    }
    private ImageView qrCard(LinearLayout parent, String text) {
        LinearLayout card = new LinearLayout(this); card.setOrientation(LinearLayout.VERTICAL); card.setGravity(Gravity.CENTER);
        card.setPadding(dp(6), dp(8), dp(6), dp(8)); parent.addView(card);
        TextView title = label(text, 15, Color.WHITE); title.setGravity(Gravity.CENTER); card.addView(title);
        ImageView image = new ImageView(this); image.setBackgroundColor(0xff24354e);
        image.setContentDescription(text + " QR code"); card.addView(image, new LinearLayout.LayoutParams(dp(160),dp(160))); return image;
    }
    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }
    private void record(String text) { log.append(text).append('\n'); }
    private void message(String title, String text) { state.setText(title); detail.setText(text); }
    private void begin() {
        if (starting || server != null) return;
        if (manager == null || channel == null || !getPackageManager().hasSystemFeature(PackageManager.FEATURE_WIFI_DIRECT)) {
            message("Direct Wi-Fi isn't exposed", "This Sony firmware does not expose the Android Wi-Fi Direct service. Open Diagnostics to see the result."); return;
        }
        String permission = Build.VERSION.SDK_INT >= 33 ? Manifest.permission.NEARBY_WIFI_DEVICES : Manifest.permission.ACCESS_FINE_LOCATION;
        if (checkSelfPermission(permission) != PackageManager.PERMISSION_GRANTED) { requestPermissions(new String[]{permission}, 10); return; }
        WifiManager wifi = (WifiManager)getApplicationContext().getSystemService(WIFI_SERVICE);
        if (wifi == null || !wifi.isWifiEnabled()) {
            new AlertDialog.Builder(this).setTitle("Turn on Wi-Fi").setMessage("Wi-Fi must be on to create the direct link. A router connection is not needed.")
                .setPositiveButton("Wi-Fi settings", (d,w) -> startActivity(new Intent(Settings.ACTION_WIFI_SETTINGS))).setNegativeButton("Cancel",null).show(); return;
        }
        if (Build.VERSION.SDK_INT <= 32 && !((LocationManager)getSystemService(LOCATION_SERVICE)).isLocationEnabled()) {
            new AlertDialog.Builder(this).setTitle("Enable Location for Wi-Fi Direct").setMessage("Android 12 requires the Location switch for Wi-Fi Direct. Drift does not collect your location.")
                .setPositiveButton("Location settings", (d,w) -> startActivity(new Intent(Settings.ACTION_LOCATION_SOURCE_SETTINGS))).setNegativeButton("Cancel",null).show(); return;
        }
        starting = true; start.setEnabled(false); stop.setEnabled(true); int run = ++generation;
        message("Checking direct Wi-Fi…", "Checking for an existing group before creating a new one.");
        try { manager.requestGroupInfo(channel, group -> {
            if (run != generation) return;
            if (group != null) { fail("An existing Wi-Fi Direct group is active", "Disconnect the other direct-sharing session first. Drift will not remove a group it did not create."); return; }
            createGroup(run, true);
        }); } catch (RuntimeException e) { fail("Wi-Fi Direct access blocked", e.toString()); }
        main.postDelayed(() -> { if (run == generation && starting) fail("Direct link timed out", "Sony did not return a usable Wi-Fi Direct group. Diagnostics contains the callbacks received."); }, 25000);
    }
    private void createGroup(int run, boolean configured) {
        if (run != generation) return;
        message("Creating a private link…", "Waiting for the TV's Wi-Fi service. This may disconnect the TV from its usual Wi-Fi.");
        WifiP2pManager.ActionListener result = new WifiP2pManager.ActionListener() {
            public void onSuccess() {
                // A delayed callback can arrive after Stop; clean up only this accepted creation.
                if (run != generation) { try { manager.removeGroup(channel, null); } catch (RuntimeException ignored) {} return; }
                ownedGroup = true; record("createGroup accepted; configured=" + configured); refreshGroup(run, 0);
            }
            public void onFailure(int reason) {
                if (run != generation) return;
                record("createGroup failed; configured=" + configured + "; reason=" + reason);
                if (configured && reason != WifiP2pManager.BUSY) { createGroup(run, false); return; }
                fail("TV declined the direct link", "Android result: " + reasonText(reason) + ". See Diagnostics. No direct connection has been established.");
            }
        };
        try {
            if (configured) {
                WifiP2pConfig config = new WifiP2pConfig.Builder().setNetworkName("DIRECT-dR-Drift")
                    .setPassphrase(TransferServer.randomToken().substring(0,16)).setGroupOperatingBand(WifiP2pConfig.GROUP_OWNER_BAND_2GHZ).build();
                manager.createGroup(channel, config, result);
            } else manager.createGroup(channel, result);
        } catch (RuntimeException e) {
            record("createGroup exception: " + e.getClass().getSimpleName());
            if (configured) createGroup(run, false); else fail("Wi-Fi Direct access blocked", e.toString());
        }
    }
    private static String reasonText(int reason) {
        return reason == WifiP2pManager.BUSY ? "BUSY (2)" : reason == WifiP2pManager.P2P_UNSUPPORTED ? "P2P_UNSUPPORTED (1)" : "ERROR (" + reason + ")";
    }
    private void refreshGroup(int run, int attempt) {
        if (run != generation || manager == null || channel == null) return;
        try { manager.requestGroupInfo(channel, group -> {
            if (run != generation) return;
            if (group == null || !group.isGroupOwner()) {
                if (server != null) { fail("Direct link disconnected", "Start a new session and scan its QR codes."); return; }
                if (starting && attempt < 12) main.postDelayed(() -> refreshGroup(run, attempt+1),1000);
                return;
            }
            if (!ownedGroup || server != null) return;
            manager.requestConnectionInfo(channel, info -> {
                if (run != generation || server != null) return;
                if (!info.groupFormed || !info.isGroupOwner || info.groupOwnerAddress == null) {
                    if (attempt < 12) main.postDelayed(() -> refreshGroup(run, attempt+1),1000); return;
                }
                if (group.getNetworkName() == null || group.getPassphrase() == null) {
                    fail("Wi-Fi credentials unavailable", "The group exists, but Sony did not expose its password. Check Diagnostics."); return;
                }
                startReceiver(run, group, info.groupOwnerAddress);
            });
        }); } catch (RuntimeException e) { fail("Wi-Fi Direct query failed", e.toString()); }
    }
    private void startReceiver(int run, WifiP2pGroup group, InetAddress address) {
        if (run != generation || server != null) return;
        try {
            NetworkInterface iface = NetworkInterface.getByName(group.getInterface());
            if (iface == null || !Collections.list(iface.getInetAddresses()).contains(address))
                throw new IOException("Group owner address is not assigned to the P2P interface");
            byte[] page;
            try (InputStream in = getAssets().open("send.html"); ByteArrayOutputStream out = new ByteArrayOutputStream()) {
                byte[] buffer = new byte[4096]; int n; while ((n = in.read(buffer)) != -1) out.write(buffer,0,n); page = out.toByteArray();
            }
            server = new TransferServer(receivedFolder(), page, new TransferServer.Events() {
                public boolean approve(String name, long size) throws InterruptedException { return askConsent(run, name, size); }
                public void progress(String name, long done, long total) {
                    runOnUiThread(() -> { if (run == generation) progress.setText(name + "\n" + formatSize(done) + " / " + formatSize(total)); });
                }
                public void received(File file, String sha) {
                    runOnUiThread(() -> { if (run == generation) {
                        progress.setText("Received " + file.getName() + "\nSHA-256: " + sha.substring(0,16) + "…");
                        new AlertDialog.Builder(MainActivity.this).setTitle("File received").setMessage(file.getName())
                            .setPositiveButton(file.getName().toLowerCase(Locale.ROOT).endsWith(".apk") ? "Install APK" : "Open", (d,w) -> openFile(file))
                            .setNegativeButton("Done", null).show();
                    }});
                }
                public void error(String message) { runOnUiThread(() -> { if (run == generation) { record(message); progress.setText(message); } }); }
            });
            int port = server.start(address, 0);
            String url = "http://" + address.getHostAddress() + ":" + port + "/#token=" + server.token();
            wifiQr.setImageBitmap(qr("WIFI:T:WPA;S:" + escapeWifi(group.getNetworkName()) + ";P:" + escapeWifi(group.getPassphrase()) + ";;"));
            sendQr.setImageBitmap(qr(url));
            networkName.setText(group.getNetworkName() + "\nPassword: " + group.getPassphrase());
            starting = false;
            message("Ready for your iPhone.", "Scan 1 to join the TV's Wi-Fi.\nScan 2 to choose files in Safari.\nAccept each file with your remote.");
            progress.setText("Pairing valid for 15 minutes.\nWPA2-protected direct Wi-Fi / HTTP transfer.");
            record("Group owner ready; interface=" + group.getInterface() + "; bound to P2P address only");
            main.postDelayed(() -> {
                if (run == generation && server != null) message("Pairing window expired", "An active upload can finish. For another file, choose Stop, then Start to generate new codes.");
            },15*60*1000L);
        } catch (Exception e) { fail("Receiver could not start", e.toString()); }
    }
    private boolean askConsent(int run, String name, long size) throws InterruptedException {
        CountDownLatch done = new CountDownLatch(1); AtomicBoolean accepted = new AtomicBoolean();
        main.post(() -> {
            if (run != generation || destroyed) { done.countDown(); return; }
            consent = new AlertDialog.Builder(this).setTitle("Receive this file?").setMessage(name + "\n" + formatSize(size))
                .setPositiveButton("Accept", (d,w) -> accepted.set(true)).setNegativeButton("Decline",null)
                .setOnDismissListener(d -> done.countDown()).create();
            consent.show();
            main.postDelayed(() -> { if (done.getCount() > 0 && consent != null) consent.dismiss(); }, 85000);
        });
        try { return done.await(90,TimeUnit.SECONDS) && accepted.get() && run == generation; }
        finally { main.post(() -> { if (consent != null && consent.isShowing()) consent.dismiss(); }); }
    }
    private Bitmap qr(String value) throws Exception {
        Map<EncodeHintType,Object> hints = new EnumMap<>(EncodeHintType.class); hints.put(EncodeHintType.MARGIN,4);
        BitMatrix matrix = new QRCodeWriter().encode(value, BarcodeFormat.QR_CODE, 640,640,hints);
        Bitmap image = Bitmap.createBitmap(640,640,Bitmap.Config.RGB_565);
        int[] pixels = new int[640*640]; for(int y=0;y<640;y++)for(int x=0;x<640;x++)pixels[y*640+x]=matrix.get(x,y)?Color.BLACK:Color.WHITE;
        image.setPixels(pixels,0,640,0,0,640,640); return image;
    }
    private static String escapeWifi(String value) { return value.replace("\\","\\\\").replace(";","\\;").replace(",","\\,").replace(":","\\:").replace("\"","\\\""); }
    private void fail(String title, String why) { record(title + ": " + why); stopSession(); message(title,why); }
    private void stopSession() {
        generation++; starting = false;
        if (consent != null) consent.dismiss();
        if (server != null) { server.close(); server = null; }
        if (ownedGroup && manager != null && channel != null) {
            try { manager.removeGroup(channel, new WifiP2pManager.ActionListener() {
                public void onSuccess() { record("Owned group removed"); }
                public void onFailure(int reason) { record("Group removal result: " + reason); }
            }); } catch (RuntimeException e) { record("Remove group: " + e.getClass().getSimpleName()); }
        }
        ownedGroup = false;
        start.setEnabled(true); stop.setEnabled(false); wifiQr.setImageDrawable(null); sendQr.setImageDrawable(null);
        networkName.setText("No active direct link"); message("Ready when you are.", "Start a direct session to generate fresh pairing codes.");
        progress.setText("Session stopped. Received files are kept.");
    }
    private File receivedFolder() { return new File(getFilesDir(), "received"); }
    private void showFiles() {
        File[] entries = receivedFolder().listFiles(f -> f.isFile() && !f.getName().startsWith("."));
        if (entries == null || entries.length == 0) { new AlertDialog.Builder(this).setMessage("No files received yet.").setPositiveButton("OK",null).show(); return; }
        Arrays.sort(entries, (a,b) -> Long.compare(b.lastModified(),a.lastModified()));
        String[] names = new String[entries.length]; for(int i=0;i<names.length;i++)names[i]=entries[i].getName()+" / "+formatSize(entries[i].length());
        new AlertDialog.Builder(this).setTitle("Received files").setItems(names,(d,which)->{
            File file=entries[which]; new AlertDialog.Builder(this).setTitle(file.getName())
                .setPositiveButton(file.getName().toLowerCase(Locale.ROOT).endsWith(".apk")?"Install APK":"Open",(dlg,w)->openFile(file))
                .setNegativeButton("Delete",(dlg,w)-> new AlertDialog.Builder(this).setTitle("Delete this file?").setMessage(file.getName())
                    .setPositiveButton("Delete",(dd,ww)-> { if(!file.delete()) Toast.makeText(this,"Could not delete file",Toast.LENGTH_LONG).show(); }).setNegativeButton("Cancel",null).show())
                .setNeutralButton("Close",null).show();
        }).setNegativeButton("Close",null).show();
    }
    private void openFile(File file) {
        boolean apk=file.getName().toLowerCase(Locale.ROOT).endsWith(".apk");
        if(apk && !getPackageManager().canRequestPackageInstalls()) {
            pendingInstall=file;
            try { startActivity(new Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,Uri.parse("package:"+getPackageName()))); }
            catch(ActivityNotFoundException e) { pendingInstall=null; Toast.makeText(this,"Enable installation from Drift in Settings",Toast.LENGTH_LONG).show(); }
            return;
        }
        Uri uri=new Uri.Builder().scheme("content").authority("dev.soum91.drift.files").appendPath(file.getName()).build();
        Intent intent=new Intent(Intent.ACTION_VIEW).setDataAndType(uri,ReceivedProvider.mime(file.getName())).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
        try { startActivity(intent); } catch(ActivityNotFoundException e) { Toast.makeText(this,"No app is installed to open this file type",Toast.LENGTH_LONG).show(); }
    }
    @Override protected void onResume() {
        super.onResume(); if(pendingInstall!=null) {File file=pendingInstall;pendingInstall=null;if(getPackageManager().canRequestPackageInstalls())openFile(file);}
    }
    private void diagnostics() { new AlertDialog.Builder(this).setTitle("Drift diagnostics").setMessage(log.toString())
        .setPositiveButton("Close",null).setNeutralButton("Wi-Fi settings",(d,w)->startActivity(new Intent(Settings.ACTION_WIFI_SETTINGS))).show(); }
    private static String formatSize(long size) {return size>=1073741824L?String.format(Locale.ROOT,"%.2f GiB",size/1073741824d):String.format(Locale.ROOT,"%.1f MiB",size/1048576d);}
    @Override public void onRequestPermissionsResult(int request,String[] permissions,int[] results) {
        super.onRequestPermissionsResult(request,permissions,results);
        if(request==10 && results.length>0 && results[0]==PackageManager.PERMISSION_GRANTED)begin();
        else message("Wi-Fi permission needed", "Grant the permission to start a direct session. No connection has been created.");
    }
    @Override protected void onDestroy() {
        destroyed=true;stopSession();main.removeCallbacksAndMessages(null);
        if(registered)unregisterReceiver(receiver);if(channel!=null)channel.close();super.onDestroy();
    }
}
