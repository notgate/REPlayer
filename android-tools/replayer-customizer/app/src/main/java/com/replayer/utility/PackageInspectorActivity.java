package com.replayer.utility;

import android.app.Activity;
import android.content.Intent;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.graphics.Typeface;
import android.net.Uri;
import android.os.Bundle;
import android.provider.Settings;
import android.view.Gravity;
import android.view.View;
import android.view.WindowManager;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import java.util.Collections;
import java.util.Comparator;
import java.util.List;

public final class PackageInspectorActivity extends Activity {
    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        getWindow().setStatusBarColor(Color.BLACK);
        getWindow().setNavigationBarColor(Color.BLACK);
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(dp(20), dp(20), dp(20), dp(12));
        root.setBackgroundColor(Color.rgb(10, 11, 12));
        setContentView(root);

        TextView heading = text("PACKAGES", 15, Color.rgb(224, 224, 224), "sans-serif-medium");
        heading.setLetterSpacing(0.16f);
        root.addView(heading, new LinearLayout.LayoutParams(-1, dp(52)));
        ScrollView scroll = new ScrollView(this);
        LinearLayout list = new LinearLayout(this);
        list.setOrientation(LinearLayout.VERTICAL);
        scroll.addView(list);
        root.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1f));

        final PackageManager pm = getPackageManager();
        List<ApplicationInfo> packages = pm.getInstalledApplications(PackageManager.MATCH_DISABLED_COMPONENTS);
        Collections.sort(packages, new Comparator<ApplicationInfo>() {
            @Override public int compare(ApplicationInfo left, ApplicationInfo right) {
                return String.valueOf(left.loadLabel(pm)).compareToIgnoreCase(String.valueOf(right.loadLabel(pm)));
            }
        });
        for (final ApplicationInfo info : packages) {
            LinearLayout row = new LinearLayout(this);
            row.setOrientation(LinearLayout.VERTICAL);
            row.setGravity(Gravity.CENTER_VERTICAL);
            row.setPadding(dp(2), dp(9), dp(2), dp(9));
            TextView name = text(String.valueOf(info.loadLabel(pm)), 14, Color.rgb(210, 212, 214), "sans-serif");
            TextView packageName = text(info.packageName, 10, Color.rgb(112, 115, 118), "monospace");
            row.addView(name);
            row.addView(packageName);
            row.setOnClickListener(new View.OnClickListener() {
                @Override public void onClick(View ignored) {
                    Intent details = new Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:" + info.packageName));
                    startActivity(details);
                }
            });
            list.addView(row, new LinearLayout.LayoutParams(-1, dp(66)));
            View separator = new View(this);
            separator.setBackgroundColor(Color.rgb(31, 33, 35));
            list.addView(separator, new LinearLayout.LayoutParams(-1, dp(1)));
        }
    }

    private TextView text(String value, int sizeSp, int color, String family) {
        TextView view = new TextView(this);
        view.setText(value);
        view.setTextSize(sizeSp);
        view.setTextColor(color);
        view.setTypeface(Typeface.create(family, Typeface.NORMAL));
        return view;
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }
}
