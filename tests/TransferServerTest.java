import dev.soum91.drift.TransferServer;
import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.concurrent.atomic.*;

public class TransferServerTest {
    static int assertions;
    static void check(boolean result,String description){assertions++;if(!result)throw new AssertionError(description);}
    static String call(int port,String path,String token,byte[] data,int expected)throws Exception{
        HttpURLConnection c=(HttpURLConnection)new URL("http://127.0.0.1:"+port+path).openConnection();
        c.setRequestMethod("POST");c.setConnectTimeout(2000);c.setReadTimeout(4000);
        c.setRequestProperty("Authorization","Bearer "+token);c.setDoOutput(true);c.setFixedLengthStreamingMode(data.length);
        try(OutputStream o=c.getOutputStream()){o.write(data);}
        int code=c.getResponseCode();check(code==expected,"Expected "+expected+", got "+code+" for "+path);
        InputStream in=code<400?c.getInputStream():c.getErrorStream();
        String result=in==null?"":new String(in.readAllBytes(),StandardCharsets.UTF_8);c.disconnect();return result;
    }
    public static void main(String[] args)throws Exception{
        File dir=Files.createTempDirectory("drift-test").toFile();
        AtomicBoolean accept=new AtomicBoolean(true);AtomicInteger received=new AtomicInteger();AtomicInteger errors=new AtomicInteger();
        TransferServer server=new TransferServer(dir,"test".getBytes(),new TransferServer.Events(){
            public boolean approve(String name,long size){return accept.get();}
            public void progress(String name,long done,long size){}
            public void received(File f,String hash){received.incrementAndGet();}
            public void error(String msg){errors.incrementAndGet();}
        });
        int port=server.start(InetAddress.getLoopbackAddress(),0);String token=server.token();byte[] empty={};
        try{
            call(port,"/offer?name=a.txt&size=3","bad",empty,403);
            call(port,"/offer?name=..%2Fescape.apk&size=3",token,empty,400);
            call(port,"/offer?name=.hidden&size=3",token,empty,400);
            call(port,"/offer?name=huge&size="+(TransferServer.MAX_FILE+1),token,empty,413);
            accept.set(false);call(port,"/offer?name=a.txt&size=3",token,empty,403);accept.set(true);
            String ticket=call(port,"/offer?name=a.txt&size=3",token,empty,200);
            call(port,"/offer?name=second.txt&size=3",token,empty,409);
            call(port,"/upload?ticket=wrong",token,"abc".getBytes(),403);
            String receipt=call(port,"/upload?ticket="+ticket,token,"abc".getBytes(),200);
            check(receipt.contains("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"),"SHA-256 matches known vector");
            check(Files.readString(new File(dir,"a.txt").toPath()).equals("abc"),"Stored bytes intact");
            call(port,"/upload?ticket="+ticket,token,"abc".getBytes(),403);
            ticket=call(port,"/offer?name=a.txt&size=3",token,empty,200);
            call(port,"/upload?ticket="+ticket,token,"def".getBytes(),200);
            check(new File(dir,"a (1).txt").isFile(),"Collision renamed");
            check(Files.readString(new File(dir,"a.txt").toPath()).equals("abc"),"Original not overwritten");
            ticket=call(port,"/offer?name=wrong.bin&size=4",token,empty,200);
            call(port,"/upload?ticket="+ticket,token,"bad".getBytes(),400);
            ticket=call(port,"/offer?name=partial.bin&size=10",token,empty,200);
            try(Socket s=new Socket("127.0.0.1",port)){
                s.getOutputStream().write(("POST /upload?ticket="+ticket+" HTTP/1.1\r\nHost: 127.0.0.1:"+port+"\r\nAuthorization: Bearer "+token+"\r\nContent-Length: 10\r\n\r\nabc").getBytes());
                s.shutdownOutput();s.getInputStream().readAllBytes();
            }
            check(!new File(dir,"partial.bin").exists(),"Interrupted file never published");
            check(dir.listFiles(f->f.getName().endsWith(".part")).length==0,"Partial data cleaned up");
            check(received.get()==2,"Only complete uploads reported");
            check(errors.get()==1,"Interrupted upload reports error");
            ticket=call(port,"/offer?name=empty.txt&size=0",token,empty,200);
            call(port,"/upload?ticket="+ticket,token,empty,200);
            check(new File(dir,"empty.txt").length()==0,"Zero-length file supported");
            ticket=call(port,"/offer?name=cancel.txt&size=3",token,empty,200);
            call(port,"/cancel",token,empty,200);
            call(port,"/upload?ticket="+ticket,token,"abc".getBytes(),403);
            check(!new File(dir,"cancel.txt").exists(),"Cancelled approval revoked");
        }finally{server.close();}
        check(!new File(dir.getParentFile(),"escape.apk").exists(),"No traversal escape");
        System.out.println("PASS: "+assertions+" transfer assertions");
    }
}
