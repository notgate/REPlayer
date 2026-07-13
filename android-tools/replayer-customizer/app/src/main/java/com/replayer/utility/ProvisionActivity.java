package com.replayer.utility;

import android.app.Activity;
import android.app.WallpaperManager;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.os.Bundle;
import android.util.Log;

public final class ProvisionActivity extends Activity {
    private static final String TAG = "HomeCustomizer";

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        try {
            Bitmap wallpaper = BitmapFactory.decodeResource(getResources(), R.drawable.premium_wallpaper);
            if (wallpaper == null) throw new IllegalStateException("Bundled wallpaper could not be decoded");
            WallpaperManager manager = WallpaperManager.getInstance(this);
            manager.setBitmap(wallpaper, null, true, WallpaperManager.FLAG_SYSTEM | WallpaperManager.FLAG_LOCK);
            getSharedPreferences("launcher", MODE_PRIVATE).edit().putInt("wallpaper", 0).apply();
            wallpaper.recycle();
            Log.i(TAG, "Dark wallpaper applied");
        } catch (Throwable error) {
            Log.e(TAG, "Wallpaper provisioning failed", error);
        } finally {
            finish();
        }
    }
}
