package dev.soum91.drift;

import android.bluetooth.*;
import android.bluetooth.le.*;
import android.content.Context;
import android.os.ParcelUuid;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.*;
import java.util.concurrent.*;

/** Opt-in, small-file BLE GATT receiver. Requires a TV with BLE peripheral advertising. */
public final class BluetoothReceiver implements Closeable {
    public interface Events {
        boolean approve(String name, long size) throws InterruptedException;
        void progress(String name, long done, long total);
        void received(File file, String sha256);
        void error(String why);
    }
    // These UUIDs are part of the Drift companion protocol; keep in sync with DriftModel.swift.
    public static final UUID SERVICE = UUID.fromString("d25e69c1-7c6d-46e3-a7c9-a7cfe89a0020");
    public static final UUID CONTROL = UUID.fromString("d25e69c1-7c6d-46e3-a7c9-a7cfe89a0021");
    public static final UUID DATA = UUID.fromString("d25e69c1-7c6d-46e3-a7c9-a7cfe89a0022");
    public static final UUID STATUS = UUID.fromString("d25e69c1-7c6d-46e3-a7c9-a7cfe89a0023");
    private static final UUID CCCD = UUID.fromString("00002902-0000-1000-8000-00805f9b34fb");
    private static final long MAX_BLE_FILE = 8L * 1024 * 1024;
    private final Context context;
    private final File folder;
    private final Events events;
    private final ExecutorService approval = Executors.newSingleThreadExecutor();
    private BluetoothGattServer server;
    private BluetoothLeAdvertiser advertiser;
    private AdvertiseSettings advertiseSettings;
    private AdvertiseData advertiseData;
    private BluetoothGattCharacteristic statusCharacteristic;
    private BluetoothDevice connected;
    private boolean subscribed, approving, closed;
    private File part;
    private FileOutputStream file;
    private MessageDigest digest;
    private String name;
    private long size, written;

    public BluetoothReceiver(Context context, File folder, Events events) {
        this.context = context; this.folder = folder; this.events = events;
    }

    @SuppressWarnings("MissingPermission")
    public synchronized void start() throws IOException {
        BluetoothManager manager = (BluetoothManager) context.getSystemService(Context.BLUETOOTH_SERVICE);
        BluetoothAdapter adapter = manager == null ? null : manager.getAdapter();
        if (adapter == null || !adapter.isEnabled()) throw new IOException("Enable Bluetooth in TV settings.");
        if (!adapter.isMultipleAdvertisementSupported() || adapter.getBluetoothLeAdvertiser() == null)
            throw new IOException("TV firmware does not support BLE peripheral advertising. Use Wi-Fi transfer.");
        if (!folder.isDirectory() && !folder.mkdirs()) throw new IOException("Cannot create received files folder");
        server = manager.openGattServer(context, callback);
        if (server == null) throw new IOException("Android rejected the GATT server");
        BluetoothGattService service = new BluetoothGattService(SERVICE, BluetoothGattService.SERVICE_TYPE_PRIMARY);
        service.addCharacteristic(new BluetoothGattCharacteristic(CONTROL, BluetoothGattCharacteristic.PROPERTY_WRITE,
            BluetoothGattCharacteristic.PERMISSION_WRITE_ENCRYPTED));
        service.addCharacteristic(new BluetoothGattCharacteristic(DATA, BluetoothGattCharacteristic.PROPERTY_WRITE,
            BluetoothGattCharacteristic.PERMISSION_WRITE_ENCRYPTED));
        statusCharacteristic = new BluetoothGattCharacteristic(STATUS,
            BluetoothGattCharacteristic.PROPERTY_NOTIFY | BluetoothGattCharacteristic.PROPERTY_READ,
            BluetoothGattCharacteristic.PERMISSION_READ_ENCRYPTED);
        statusCharacteristic.addDescriptor(new BluetoothGattDescriptor(CCCD,
            BluetoothGattDescriptor.PERMISSION_READ | BluetoothGattDescriptor.PERMISSION_WRITE_ENCRYPTED));
        statusCharacteristic.setValue("WAITING".getBytes(StandardCharsets.UTF_8));
        service.addCharacteristic(statusCharacteristic);
        advertiser = adapter.getBluetoothLeAdvertiser();
        advertiseSettings = new AdvertiseSettings.Builder().setAdvertiseMode(AdvertiseSettings.ADVERTISE_MODE_LOW_LATENCY)
            .setConnectable(true).setTxPowerLevel(AdvertiseSettings.ADVERTISE_TX_POWER_MEDIUM).build();
        advertiseData = new AdvertiseData.Builder().setIncludeDeviceName(false).addServiceUuid(new ParcelUuid(SERVICE)).build();
        // Only advertise after the asynchronous GATT service registration succeeds.
        if (!server.addService(service)) throw new IOException("Could not publish BLE transfer characteristics");
    }

    private final AdvertiseCallback advertiseCallback = new AdvertiseCallback() {
        @Override public void onStartFailure(int code) { events.error("BLE advertising failed (" + code + "). Use Wi-Fi."); }
    };

    private final BluetoothGattServerCallback callback = new BluetoothGattServerCallback() {
        @Override public void onServiceAdded(int state, BluetoothGattService service) {
            synchronized (BluetoothReceiver.this) {
                if (closed || !SERVICE.equals(service.getUuid())) return;
                if (state == BluetoothGatt.GATT_SUCCESS && advertiser != null)
                    advertiser.startAdvertising(advertiseSettings,advertiseData,advertiseCallback);
                else events.error("Android could not register the BLE service. Use Wi-Fi.");
            }
        }
        @Override public void onConnectionStateChange(BluetoothDevice device, int state, int newState) {
            synchronized (BluetoothReceiver.this) {
                if (newState == BluetoothProfile.STATE_CONNECTED) {
                    if (connected != null && !connected.getAddress().equals(device.getAddress())) {
                        if (server != null) server.cancelConnection(device);
                    } else connected = device;
                } else if (connected != null && connected.getAddress().equals(device.getAddress())) {
                    connected = null; subscribed = false; approving = false; discard();
                }
            }
        }
        @Override public void onDescriptorWriteRequest(BluetoothDevice device, int requestId, BluetoothGattDescriptor descriptor,
                boolean prepared, boolean responseNeeded, int offset, byte[] value) {
            int result = BluetoothGatt.GATT_FAILURE;
            synchronized (BluetoothReceiver.this) {
                if (!prepared && offset == 0 && CCCD.equals(descriptor.getUuid()) && device.equals(connected)) {
                    subscribed = Arrays.equals(value, BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE);
                    result = BluetoothGatt.GATT_SUCCESS;
                }
            }
            if (responseNeeded && server != null) server.sendResponse(device,requestId,result,0,null);
        }
        @Override public void onCharacteristicReadRequest(BluetoothDevice device,int requestId,int offset,BluetoothGattCharacteristic c) {
            byte[] value = STATUS.equals(c.getUuid()) ? c.getValue() : new byte[0];
            if (server != null) server.sendResponse(device,requestId,offset<=value.length ? BluetoothGatt.GATT_SUCCESS : BluetoothGatt.GATT_INVALID_OFFSET,
                offset<=value.length ? offset : 0,offset<=value.length ? Arrays.copyOfRange(value,offset,value.length) : null);
        }
        @Override public void onCharacteristicWriteRequest(BluetoothDevice device,int requestId,BluetoothGattCharacteristic c,
                boolean prepared,boolean responseNeeded,int offset,byte[] value) {
            int result = BluetoothGatt.GATT_FAILURE;
            synchronized (BluetoothReceiver.this) {
                if (!closed && device.equals(connected) && !prepared && offset == 0 && value != null) {
                    if (CONTROL.equals(c.getUuid())) result = command(value);
                    else if (DATA.equals(c.getUuid())) result = chunk(value);
                }
            }
            if (responseNeeded && server != null) server.sendResponse(device,requestId,result,0,null);
        }
    };

    private int command(byte[] payload) {
        String command = new String(payload, StandardCharsets.UTF_8);
        if ("CANCEL".equals(command)) { discard(); approving = false; publish("CANCELLED"); return BluetoothGatt.GATT_SUCCESS; }
        if ("FINISH".equals(command)) {
            if (file == null || written != size) { discard(); publish("ERROR:Incomplete file"); return BluetoothGatt.GATT_FAILURE; }
            try {
                file.getFD().sync(); file.close(); file = null;
                File dest = uniqueDestination(name);
                Files.move(part.toPath(),dest.toPath()); part = null;
                StringBuilder sha = new StringBuilder(); for (byte b : digest.digest()) sha.append(String.format(Locale.ROOT,"%02x",b & 255));
                events.received(dest,sha.toString()); publish("DONE:" + sha); reset();
                return BluetoothGatt.GATT_SUCCESS;
            } catch (IOException e) { discard(); publish("ERROR:Save failed"); return BluetoothGatt.GATT_FAILURE; }
        }
        if (!command.startsWith("START:" ) || approving || file != null) return BluetoothGatt.GATT_FAILURE;
        int split = command.lastIndexOf(':');
        try {
            final String requestedName = TransferServer.safeName(command.substring(6,split));
            final long requestedSize = Long.parseLong(command.substring(split+1));
            if (requestedSize < 0 || requestedSize > MAX_BLE_FILE || requestedSize > folder.getUsableSpace()-16*1024*1024)
                return BluetoothGatt.GATT_FAILURE;
            approving = true; publish("APPROVAL");
            approval.execute(() -> {
                boolean allowed = false;
                try { allowed = events.approve(requestedName,requestedSize); }
                catch (InterruptedException ignored) { Thread.currentThread().interrupt(); }
                synchronized (BluetoothReceiver.this) {
                    if (closed || !approving || connected == null) return;
                    approving = false;
                    if (!allowed) { publish("DECLINED"); return; }
                    try {
                        part = File.createTempFile(".drift-",".part",folder);
                        file = new FileOutputStream(part);
                        digest = MessageDigest.getInstance("SHA-256");
                        name = requestedName; size = requestedSize; written = 0;
                        publish("READY");
                    } catch (IOException | NoSuchAlgorithmException e) { discard(); publish("ERROR:Cannot open file"); }
                }
            });
            return BluetoothGatt.GATT_SUCCESS;
        } catch (Exception e) { return BluetoothGatt.GATT_FAILURE; }
    }

    private int chunk(byte[] payload) {
        if (file == null || payload.length > size-written) return BluetoothGatt.GATT_FAILURE;
        try {
            file.write(payload); digest.update(payload); written += payload.length;
            if (written == size || (written & 65535) < payload.length) events.progress(name,written,size);
            return BluetoothGatt.GATT_SUCCESS;
        } catch (IOException e) { discard(); publish("ERROR:Write failed"); return BluetoothGatt.GATT_FAILURE; }
    }

    @SuppressWarnings("MissingPermission")
    private void publish(String value) {
        if (statusCharacteristic == null) return;
        statusCharacteristic.setValue(value.getBytes(StandardCharsets.UTF_8));
        if (subscribed && connected != null && server != null) server.notifyCharacteristicChanged(connected,statusCharacteristic,false);
    }
    private File uniqueDestination(String filename) throws IOException {
        String stem = filename, ext = ""; int dot = filename.lastIndexOf('.');
        if (dot > 0) { stem = filename.substring(0,dot);ext=filename.substring(dot); }
        File dest = new File(folder,filename);
        for (int n=1; dest.exists();n++) {
            if (n>9999) throw new IOException("Duplicate file limit reached");
            dest = new File(folder,stem+" ("+n+")"+ext);
        }
        return dest;
    }
    private void reset() { name = null; size = 0; written = 0; digest = null; }
    private void discard() {
        if (file != null) try { file.close(); } catch (IOException ignored) {}
        file = null; if (part != null) part.delete(); part = null; reset();
    }
    @Override @SuppressWarnings("MissingPermission") public synchronized void close() {
        closed = true; approving = false; discard(); approval.shutdownNow();
        if (advertiser != null) advertiser.stopAdvertising(advertiseCallback);
        if (server != null) server.close();
        advertiser = null; server = null; connected = null; subscribed = false;
    }
}
