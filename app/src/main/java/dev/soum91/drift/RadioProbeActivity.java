package dev.soum91.drift;

import android.app.*;
import android.content.*;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.hardware.usb.*;
import android.os.*;
import android.util.Log;
import android.view.*;
import android.widget.*;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.*;

/** Read-only USB visibility/permission probe. Never claims an interface or transmits RF. */
public final class RadioProbeActivity extends Activity {
    private static final String ACTION_USB = "dev.soum91.drift.USB_PROBE_PERMISSION";
    private UsbManager usb;
    private TextView report, result;
    private boolean registered;
    private final BroadcastReceiver permissions = new BroadcastReceiver() {
        @Override public void onReceive(Context context, Intent intent) {
            if (!ACTION_USB.equals(intent.getAction())) return;
            // Re-enumerate the device by name. Do not trust parcelled interface data.
            UsbDevice requested = intent.getParcelableExtra(UsbManager.EXTRA_DEVICE);
            UsbDevice device = null;
            if (requested != null && usb != null) device = usb.getDeviceList().get(requested.getDeviceName());
            if (device == null) { result.setText("The selected USB device is no longer available."); return; }
            if (!usb.hasPermission(device)) { result.setText("USB permission was not granted. Radio access is unproven."); return; }
            readDescriptors(device);
        }
    };
    @Override public void onCreate(Bundle saved) {
        super.onCreate(saved);
        usb = (UsbManager)getSystemService(USB_SERVICE);
        LinearLayout root = new LinearLayout(this); root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(40,24,40,24); root.setBackgroundColor(0xff101827);
        TextView title = text("DRIFT / NATIVE AIRDROP RADIO INVESTIGATION",22,0xff6fe4bd); root.addView(title);
        TextView explanation = text("Build 2 checks access to the internal Wi-Fi radio. It does not transfer files or advertise AirDrop.\nRefresh reads device details; Test USB access asks permission and reads descriptors only.",16,0xffc3cfe0); root.addView(explanation);
        LinearLayout actions = new LinearLayout(this); root.addView(actions);
        Button refresh = button("Refresh radio details", () -> refresh()); actions.addView(refresh);
        actions.addView(button("Test USB access", () -> chooseDevice()));
        actions.addView(button("Copy diagnostic text", () -> {
            ((android.content.ClipboardManager)getSystemService(CLIPBOARD_SERVICE)).setPrimaryClip(ClipData.newPlainText("Drift radio diagnostics",report.getText()+"\n"+result.getText()));
            Toast.makeText(this,"Copied on the TV",Toast.LENGTH_SHORT).show();
        }));
        result = text("No interface will be claimed or detached. Wi-Fi settings remain unchanged.",16,0xff6fe4bd); root.addView(result);
        ScrollView scroll = new ScrollView(this); root.addView(scroll,new LinearLayout.LayoutParams(-1,0,1));
        report = text("",15,Color.WHITE); report.setTypeface(android.graphics.Typeface.MONOSPACE); report.setTextIsSelectable(true);scroll.addView(report);
        setContentView(root); refresh.requestFocus();
        IntentFilter filter = new IntentFilter(ACTION_USB);
        if (Build.VERSION.SDK_INT >= 33) registerReceiver(permissions,filter,Context.RECEIVER_NOT_EXPORTED);else registerReceiver(permissions,filter);
        registered=true; refresh();
    }
    private TextView text(String value,int size,int color) {
        TextView v=new TextView(this);v.setText(value);v.setTextSize(size);v.setTextColor(color);v.setPadding(0,6,0,6);return v;
    }
    private Button button(String label,Runnable action) {
        Button b=new Button(this);b.setText(label);b.setAllCaps(false);b.setOnClickListener(v->action.run());
        b.setOnFocusChangeListener((v,f)->{b.setTextColor(f?0xff101827:Color.WHITE);b.setBackgroundTintList(android.content.res.ColorStateList.valueOf(f?0xff6fe4bd:0xff24354e));});
        b.setTextColor(Color.WHITE);b.setBackgroundTintList(android.content.res.ColorStateList.valueOf(0xff24354e));return b;
    }
    private List<UsbDevice> devices() {
        List<UsbDevice> list=new ArrayList<>();
        if(usb!=null)list.addAll(usb.getDeviceList().values());
        list.sort(Comparator.comparing(UsbDevice::getDeviceName));return list;
    }
    private void refresh() {
        StringBuilder out=new StringBuilder();
        out.append(Build.MODEL).append(" / Android ").append(Build.VERSION.RELEASE).append(" / API ").append(Build.VERSION.SDK_INT).append('\n');
        out.append("ABIs: ").append(Arrays.toString(Build.SUPPORTED_ABIS)).append('\n');
        out.append("Kernel: ").append(System.getProperty("os.version")).append('\n');
        out.append("App UID: ").append(android.os.Process.myUid()).append(" (not the ADB shell)\n");
        out.append("USB host feature: ").append(getPackageManager().hasSystemFeature(PackageManager.FEATURE_USB_HOST)).append('\n');
        out.append("App-visible USB devices:\n");
        List<UsbDevice> list=devices();
        if(list.isEmpty())out.append("  NONE. This does not prove that no internal USB radio exists; Sony may hide it from apps.\n");
        for(UsbDevice d:list){
            out.append("  ").append(id(d)).append(" ").append(safe(d.getManufacturerName())).append(" / ").append(safe(d.getProductName())).append('\n');
            out.append("  permission=").append(usb.hasPermission(d)).append(" configurations=").append(d.getConfigurationCount()).append('\n');
            for(int i=0;i<d.getInterfaceCount();i++){
                UsbInterface f=d.getInterface(i);boolean in=false,outEndpoint=false;
                for(int j=0;j<f.getEndpointCount();j++){
                    UsbEndpoint ep=f.getEndpoint(j);
                    if(ep.getType()==UsbConstants.USB_ENDPOINT_XFER_BULK){if(ep.getDirection()==UsbConstants.USB_DIR_IN)in=true;else outEndpoint=true;}
                }
                out.append("    IF ").append(f.getId()).append(" class=").append(f.getInterfaceClass()).append(" sub=").append(f.getInterfaceSubclass())
                    .append(" endpoints=").append(f.getEndpointCount()).append(" bulk RX/TX=").append(in).append('/').append(outEndpoint).append('\n');
            }
        }
        out.append("\nNetwork interface driver links (if readable):\n");
        File[] nets=new File("/sys/class/net").listFiles();
        if(nets==null)out.append("  unavailable to app\n");else for(File n:nets){
            try{File link=new File(n,"device/driver");String path=link.getCanonicalPath();out.append("  ").append(n.getName()).append(": ").append(link.exists()?new File(path).getName():"not exposed").append('\n');}
            catch(IOException|SecurityException e){out.append("  ").append(n.getName()).append(": access restricted\n");}
        }
        out.append("\nReadable USB sysfs identities:\n");
        File[] entries=new File("/sys/bus/usb/devices").listFiles();int count=0;
        if(entries!=null)for(File entry:entries){
            String vid=read(new File(entry,"idVendor")),pid=read(new File(entry,"idProduct"));
            if(!vid.isEmpty()&&!pid.isEmpty()){out.append("  ").append(entry.getName()).append(' ').append(vid).append(':').append(pid).append(' ').append(read(new File(entry,"product"))).append('\n');count++;}
        }
        if(count==0)out.append("  No identities readable by this app\n");
        out.append("\nResult: USB visibility/opening only; no RF reception, AWDL, or AirDrop test has run.");
        report.setText(out);for(String line:out.toString().split("\n"))Log.i("DriftRadio",line);
    }
    private void chooseDevice() {
        List<UsbDevice> list=devices();
        if(list.isEmpty()){result.setText("Sony exposes no USB devices to Drift. ADB USB/sysfs diagnostics are needed to distinguish hidden hardware from no USB radio.");return;}
        String[] names=new String[list.size()];for(int i=0;i<names.length;i++)names[i]=id(list.get(i))+" / "+safe(list.get(i).getProductName());
        new AlertDialog.Builder(this).setTitle("Read-only USB descriptor test").setItems(names,(dialog,which)->{
            UsbDevice d=list.get(which);
            if(usb.hasPermission(d))readDescriptors(d);else{
                Intent intent=new Intent(ACTION_USB).setPackage(getPackageName());
                // Mutable is needed only for the system to attach its permission result.
                PendingIntent callback=PendingIntent.getBroadcast(this,0,intent,PendingIntent.FLAG_UPDATE_CURRENT|PendingIntent.FLAG_MUTABLE);
                try{usb.requestPermission(d,callback);result.setText("Waiting for Android's USB permission prompt for "+id(d));}
                catch(RuntimeException e){result.setText("Permission request blocked: "+e.getClass().getSimpleName());}
            }
        }).setNegativeButton("Cancel",null).show();
    }
    private void readDescriptors(UsbDevice d) {
        UsbDeviceConnection connection=null;
        try{
            connection=usb.openDevice(d);
            if(connection==null){result.setText(id(d)+": permission granted, but opening returned null.");return;}
            byte[] descriptors=connection.getRawDescriptors();
            String value=id(d)+": OPEN OK / descriptors="+(descriptors==null?0:descriptors.length)+" bytes.\nNo interface was claimed. Raw radio access and AWDL remain untested.";
            result.setText(value);Log.i("DriftRadio",value);
        }catch(RuntimeException e){result.setText(id(d)+": open failed: "+e.getClass().getSimpleName());}
        finally{if(connection!=null)connection.close();}
        refresh();
    }
    private static String id(UsbDevice d){return String.format(Locale.ROOT,"%04x:%04x",d.getVendorId(),d.getProductId());}
    private static String safe(String value){return value==null?"unknown":value.replaceAll("[\\p{Cntrl}]"," ");}
    private static String read(File path){
        try(FileInputStream in=new FileInputStream(path)){byte[] bytes=new byte[256];int n=in.read(bytes);return n<0?"":safe(new String(bytes,0,n,StandardCharsets.UTF_8).trim());}
        catch(IOException|SecurityException e){return "";}
    }
    @Override protected void onDestroy(){if(registered)unregisterReceiver(permissions);super.onDestroy();}
}
