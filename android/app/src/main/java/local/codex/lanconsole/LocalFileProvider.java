package local.codex.lanconsole;

import android.content.ContentProvider;
import android.content.ContentValues;
import android.content.Context;
import android.database.Cursor;
import android.database.MatrixCursor;
import android.net.Uri;
import android.os.Environment;
import android.os.ParcelFileDescriptor;
import android.provider.OpenableColumns;
import android.webkit.MimeTypeMap;

import java.io.File;
import java.io.FileNotFoundException;
import java.io.IOException;
import java.util.List;
import java.util.Locale;

/**
 * A deliberately small file provider for camera captures and completed downloads.
 * It avoids a broad storage grant and exposes only the two explicit directories below.
 */
public final class LocalFileProvider extends ContentProvider {
    private static final String CAPTURE = "capture";
    private static final String DOWNLOAD = "download";

    public static Uri uriForCapture(Context context, File file) {
        return uriForFile(context, CAPTURE, captureRoot(context), file);
    }

    public static Uri uriForDownload(Context context, File file) {
        return uriForFile(context, DOWNLOAD, downloadRoot(), file);
    }

    private static Uri uriForFile(Context context, String kind, File root, File file) {
        try {
            File canonicalRoot = root.getCanonicalFile();
            File canonicalFile = file.getCanonicalFile();
            if (!canonicalRoot.equals(canonicalFile.getParentFile())) {
                throw new IllegalArgumentException("File is outside the shared directory");
            }
            return new Uri.Builder()
                    .scheme("content")
                    .authority(context.getPackageName() + ".files")
                    .appendPath(kind)
                    .appendPath(canonicalFile.getName())
                    .build();
        } catch (IOException error) {
            throw new IllegalArgumentException("Cannot resolve shared file", error);
        }
    }

    public static File captureRoot(Context context) {
        File root = new File(context.getCacheDir(), "captures");
        if (!root.exists() && !root.mkdirs()) {
            throw new IllegalStateException("Cannot create capture directory");
        }
        return root;
    }

    public static File downloadRoot() {
        File root = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS);
        if (!root.exists() && !root.mkdirs()) {
            throw new IllegalStateException("Cannot create Downloads directory");
        }
        return root;
    }

    @Override
    public boolean onCreate() {
        return true;
    }

    @Override
    public String getType(Uri uri) {
        File file = resolve(uri);
        String extension = MimeTypeMap.getFileExtensionFromUrl(Uri.encode(file.getName()));
        String mime = MimeTypeMap.getSingleton()
                .getMimeTypeFromExtension(
                        extension == null ? "" : extension.toLowerCase(Locale.ROOT));
        return mime == null ? "application/octet-stream" : mime;
    }

    @Override
    public Cursor query(
            Uri uri,
            String[] projection,
            String selection,
            String[] selectionArgs,
            String sortOrder) {
        File file = resolve(uri);
        String[] columns = projection == null
                ? new String[]{OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE}
                : projection;
        MatrixCursor cursor = new MatrixCursor(columns, 1);
        Object[] row = new Object[columns.length];
        for (int index = 0; index < columns.length; index++) {
            if (OpenableColumns.DISPLAY_NAME.equals(columns[index])) {
                row[index] = file.getName();
            } else if (OpenableColumns.SIZE.equals(columns[index])) {
                row[index] = file.length();
            }
        }
        cursor.addRow(row);
        return cursor;
    }

    @Override
    public ParcelFileDescriptor openFile(Uri uri, String mode) throws FileNotFoundException {
        File file = resolve(uri);
        List<String> segments = uri.getPathSegments();
        boolean capture = segments.size() == 2 && CAPTURE.equals(segments.get(0));
        int flags;
        if ("r".equals(mode)) {
            flags = ParcelFileDescriptor.MODE_READ_ONLY;
        } else if (capture && ("w".equals(mode) || "wt".equals(mode) || "rwt".equals(mode))) {
            flags = ParcelFileDescriptor.MODE_WRITE_ONLY
                    | ParcelFileDescriptor.MODE_CREATE
                    | ParcelFileDescriptor.MODE_TRUNCATE;
            if ("rwt".equals(mode)) {
                flags = ParcelFileDescriptor.MODE_READ_WRITE
                        | ParcelFileDescriptor.MODE_CREATE
                        | ParcelFileDescriptor.MODE_TRUNCATE;
            }
        } else if (capture && "wa".equals(mode)) {
            flags = ParcelFileDescriptor.MODE_WRITE_ONLY
                    | ParcelFileDescriptor.MODE_CREATE
                    | ParcelFileDescriptor.MODE_APPEND;
        } else if (capture && "rw".equals(mode)) {
            flags = ParcelFileDescriptor.MODE_READ_WRITE | ParcelFileDescriptor.MODE_CREATE;
        } else {
            throw new FileNotFoundException("Unsupported mode");
        }
        return ParcelFileDescriptor.open(file, flags);
    }

    @Override
    public Uri insert(Uri uri, ContentValues values) {
        throw new UnsupportedOperationException("Insert is not supported");
    }

    @Override
    public int delete(Uri uri, String selection, String[] selectionArgs) {
        return 0;
    }

    @Override
    public int update(Uri uri, ContentValues values, String selection, String[] selectionArgs) {
        return 0;
    }

    private File resolve(Uri uri) {
        Context context = getContext();
        if (context == null || !context.getPackageName().concat(".files").equals(uri.getAuthority())) {
            throw new SecurityException("Unknown authority");
        }

        List<String> segments = uri.getPathSegments();
        if (segments.size() != 2) {
            throw new SecurityException("Invalid path");
        }

        File root;
        if (CAPTURE.equals(segments.get(0))) {
            root = captureRoot(context);
        } else if (DOWNLOAD.equals(segments.get(0))) {
            root = downloadRoot();
        } else {
            throw new SecurityException("Invalid root");
        }

        try {
            File canonicalRoot = root.getCanonicalFile();
            File candidate = new File(canonicalRoot, segments.get(1)).getCanonicalFile();
            if (!canonicalRoot.equals(candidate.getParentFile())) {
                throw new SecurityException("Path escapes shared root");
            }
            return candidate;
        } catch (IOException error) {
            throw new SecurityException("Cannot resolve path", error);
        }
    }
}
