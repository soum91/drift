package dev.soum91.drift;

import android.content.*;
import android.database.*;
import android.net.Uri;
import android.os.ParcelFileDescriptor;
import android.provider.OpenableColumns;
import android.webkit.MimeTypeMap;
import java.io.*;
import java.util.Locale;

/** Read-only, temporary URI grants for completed received files. */
public final class ReceivedProvider extends ContentProvider {
    public boolean onCreate(){return true;}
    private File resolve(Uri uri) throws FileNotFoundException {
        try {
            if(uri.getPathSegments().size()!=1)throw new IOException();
            String name=TransferServer.safeName(uri.getLastPathSegment());
            File folder=new File(getContext().getFilesDir(),"received").getCanonicalFile();
            File file=new File(folder,name).getCanonicalFile();
            if(!folder.equals(file.getParentFile())||!file.isFile())throw new IOException();
            return file;
        }catch(IOException e){throw new FileNotFoundException("File unavailable");}
    }
    public static String mime(String name){
        if(name.toLowerCase(Locale.ROOT).endsWith(".apk"))return "application/vnd.android.package-archive";
        int dot=name.lastIndexOf('.');String type=dot<0?null:MimeTypeMap.getSingleton().getMimeTypeFromExtension(name.substring(dot+1).toLowerCase(Locale.ROOT));
        return type==null?"application/octet-stream":type;
    }
    public String getType(Uri uri){return mime(uri.getLastPathSegment()==null?"":uri.getLastPathSegment());}
    public ParcelFileDescriptor openFile(Uri uri,String mode)throws FileNotFoundException{
        if(!"r".equals(mode))throw new FileNotFoundException("Read only");return ParcelFileDescriptor.open(resolve(uri),ParcelFileDescriptor.MODE_READ_ONLY);
    }
    public Cursor query(Uri uri,String[] projection,String selection,String[] args,String sort){
        try{File file=resolve(uri);String[] cols=projection==null?new String[]{OpenableColumns.DISPLAY_NAME,OpenableColumns.SIZE}:projection;
            MatrixCursor cursor=new MatrixCursor(cols,1);Object[] row=new Object[cols.length];
            for(int i=0;i<cols.length;i++)row[i]=OpenableColumns.DISPLAY_NAME.equals(cols[i])?file.getName():OpenableColumns.SIZE.equals(cols[i])?file.length():null;
            cursor.addRow(row);return cursor;
        }catch(FileNotFoundException e){return null;}
    }
    public Uri insert(Uri uri,ContentValues values){throw new UnsupportedOperationException();}
    public int delete(Uri uri,String selection,String[] args){throw new UnsupportedOperationException();}
    public int update(Uri uri,ContentValues values,String selection,String[] args){throw new UnsupportedOperationException();}
}
