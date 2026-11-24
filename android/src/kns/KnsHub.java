package kns;

import android.app.Activity;
import android.app.AlertDialog;
import android.app.PendingIntent;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.SharedPreferences;
import android.graphics.Color;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.telephony.TelephonyManager;
import android.text.InputType;
import android.view.View;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.TextView;

import com.com2us.peppermint.HubBridge;

import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.Charset;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.UUID;

/**
 * Android counterpart of the browser build's HubBridge (browser/port/java_shim.js) and account panel
 * (browser/port/account.js). The Com2uS Hub and HIVE login screens are gone: the title's Log In button opens an
 * ID/password dialog that uses the KnS server's own /account/login and /account/register, and every Hub request goes to the
 * server's /hub/ routes under one stable device id. An account is a device id on the server, as in the browser.
 *
 * HubBridge's public methods delegate here (apk-build/patch_android.py). Callbacks go back through
 * HubBridge.HubCallbackWithJSON, which queues them for the game thread.
 */
public final class KnsHub {
    /** Replaced by the build with the server API base from android/config.json, e.g. https://example.com/kns/api. */
    private static final String BASE = "@KNS_BASE@";
    private static final Charset UTF8 = Charset.forName("UTF-8");
    /** hubCallbackAuth gives this code no error popup. */
    private static final int LOGIN_CANCELLED = 2001;
    private static final String PREFS = "kns_account";

    private static Object utility;
    private static final Handler MAIN = new Handler(Looper.getMainLooper());

    private KnsHub() {
    }

    // ---------- the game's property store (CUtility), where the native reads hub_guest_uid ----------

    /** Called from CUtility's constructor, so the game's own property store can be read and changed. */
    public static void setUtility(Object instance) {
        utility = instance;
        syncProgressEpoch(contextOf(instance));
    }

    // ---------- progress reset (the browser's account.js syncProgressEpoch) ----------

    private static Context contextOf(Object instance) {
        if (HubBridge.mainActivity != null) return HubBridge.mainActivity;
        for (Field f : instance.getClass().getDeclaredFields()) {
            if (!Context.class.isAssignableFrom(f.getType())) continue;
            try {
                f.setAccessible(true);
                Object v = f.get(instance);
                if (v != null) return (Context) v;
            } catch (Exception ignored) {
                // try the next field
            }
        }
        return null;
    }

    /**
     * After a server-wide progress reset (tools/reset_progress.py) the server's /api/progress epoch changes: drop the native game
     * save once (tutorial state, local flags) so it starts as fresh as the server data. Runs while CUtility is built, before the
     * native loads its save; waits at most 6 s for the server and changes nothing when it cannot be reached.
     */
    private static void syncProgressEpoch(Context ctx) {
        if (ctx == null) return;
        final java.util.concurrent.atomic.AtomicLong epoch = new java.util.concurrent.atomic.AtomicLong();
        Thread t = new Thread(new Runnable() {
            @Override
            public void run() {
                try {
                    epoch.set(post("/api/progress", new JSONObject(), "text/plain").optLong("progress_epoch", 0));
                } catch (Exception ignored) {
                    // offline: keep the save
                }
            }
        }, "kns-progress");
        t.start();
        try {
            t.join(6000);
        } catch (InterruptedException ignored) {
            Thread.currentThread().interrupt();
        }
        long e = epoch.get();
        if (e <= 0) return;
        SharedPreferences p = ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        if (p.getLong("progress_epoch", 0) == e) return;
        wipeGameSave(ctx);
        p.edit().putLong("progress_epoch", e).commit();
    }

    /** The native save: Hero1MSys and Hero1M_<user> in files<package>/, and 1.4.0/dt_<user>_stagearea.dat. Downloaded data stays. */
    private static void wipeGameSave(Context ctx) {
        java.io.File files = ctx.getFilesDir();
        String[] packages = {ctx.getPackageName(), "com.com2us.imhero.normal.freefull.google.global.android.common"};
        for (String pkg : packages) {
            for (java.io.File dir : new java.io.File[]{new java.io.File(files.getPath() + pkg), new java.io.File(files, pkg)}) {
                java.io.File[] list = dir.listFiles();
                if (list == null) continue;
                for (java.io.File f : list) {
                    String n = f.getName();
                    if (f.isFile() && (n.equals("Hero1MSys") || n.startsWith("Hero1M_")) && !f.delete()) {
                        android.util.Log.w("KNS", "could not delete " + f);
                    }
                }
            }
        }
        java.io.File[] data = new java.io.File(files, "1.4.0").listFiles();
        if (data == null) return;
        for (java.io.File f : data) {
            if (f.isFile() && f.getName().matches("dt_\\d+_stagearea\\.dat") && !f.delete()) {
                android.util.Log.w("KNS", "could not delete " + f);
            }
        }
    }

    private static Object callUtility(String name, Class<?>[] types, Object... args) {
        if (utility == null) return null;
        try {
            Method m = utility.getClass().getMethod(name, types);
            return m.invoke(utility, args);
        } catch (Exception e) {
            return null;
        }
    }

    private static String prop(String key) {
        Object v = callUtility("getAppProperty", new Class<?>[]{byte[].class}, (Object) key.getBytes(UTF8));
        return v instanceof byte[] ? new String((byte[]) v, UTF8) : null;
    }

    private static void setProp(String key, String value) {
        callUtility("setAppProperty", new Class<?>[]{byte[].class, byte[].class}, key.getBytes(UTF8), value.getBytes(UTF8));
    }

    private static void delProp(String key) {
        callUtility("removeAppProperty", new Class<?>[]{byte[].class}, (Object) key.getBytes(UTF8));
    }

    /** Drops every saved game property, so the next player starts clean (the browser's forgetLocal). */
    private static void forgetProps() {
        if (utility == null) return;
        try {
            for (Field f : utility.getClass().getDeclaredFields()) {
                if (f.getType() != HashMap.class) continue;
                f.setAccessible(true);
                HashMap<?, ?> map = (HashMap<?, ?>) f.get(utility);
                if (map == null) continue;
                for (Object key : new ArrayList<Object>(map.keySet())) {
                    if (key instanceof String) delProp((String) key);
                }
            }
        } catch (Exception ignored) {
            // nothing more to forget
        }
    }

    // ---------- this app's account settings ----------

    private static Activity activity() {
        return HubBridge.mainActivity;
    }

    private static SharedPreferences prefs() {
        return activity().getSharedPreferences(PREFS, Context.MODE_PRIVATE);
    }

    private static String get(String key) {
        return prefs().getString(key, null);
    }

    private static String did() {
        String d = get("web_did");
        if (d == null) {
            // Keep <= 28 chars to fit the native fixed device-id buffer and PasswordAccountService.DevicePattern().
            d = "and-" + UUID.randomUUID().toString().replace("-", "").substring(0, 16);
            prefs().edit().putString("web_did", d).commit();
        }
        return d;
    }

    // ---------- server ----------

    private static JSONObject post(String path, JSONObject body, String type) throws Exception {
        HttpURLConnection c = (HttpURLConnection) new URL(BASE + path).openConnection();
        try {
            c.setConnectTimeout(15000);
            c.setReadTimeout(30000);
            c.setDoOutput(true);
            c.setRequestMethod("POST");
            c.setRequestProperty("Content-Type", type);
            byte[] out = body.toString().getBytes(UTF8);
            c.setFixedLengthStreamingMode(out.length);
            OutputStream os = c.getOutputStream();
            os.write(out);
            os.close();
            int code = c.getResponseCode();
            InputStream in = code < 400 ? c.getInputStream() : c.getErrorStream();
            String text = "";
            if (in != null) {
                ByteArrayOutputStream buf = new ByteArrayOutputStream();
                byte[] chunk = new byte[4096];
                for (int n; (n = in.read(chunk)) > 0; ) buf.write(chunk, 0, n);
                in.close();
                text = new String(buf.toByteArray(), UTF8);
            }
            JSONObject j;
            try {
                j = text.length() == 0 ? new JSONObject() : new JSONObject(text);
            } catch (Exception e) {
                j = new JSONObject();
            }
            if (code >= 400) {
                String error = j.optString("error", "");
                throw new Exception(error.length() > 0 ? error : "The server said no (HTTP " + code + ").");
            }
            return j;
        } finally {
            c.disconnect();
        }
    }

    private static JSONObject hubPost(String name, JSONObject params) throws Exception {
        return post("/hub/" + name, params, "application/json");
    }

    private static JSONObject accountPost(String path, JSONObject params) throws Exception {
        try {
            return post(path, params, "text/plain");
        } catch (java.io.IOException e) {
            throw new Exception("Cannot reach the game server. Check your internet connection.");
        }
    }

    private static void callback(JSONObject json, int cb, int arg) {
        HubBridge.HubCallbackWithJSON(json, cb, arg);
    }

    private static void callback(int errorCode, int cb, int arg) {
        try {
            callback(new JSONObject().put("error_code", errorCode), cb, arg);
        } catch (Exception ignored) {
            // JSONObject.put only throws for invalid numbers
        }
    }

    private static void fail(String name, Exception e, int cb, int arg) {
        android.util.Log.w("KNS", "hub " + name + " failed: " + e);
        try {
            callback(new JSONObject().put("error_code", 1).put("error_detail", String.valueOf(e.getMessage())), cb, arg);
        } catch (Exception ignored) {
            // see callback(int, ...)
        }
    }

    private static void background(Runnable r) {
        new Thread(r, "kns-hub").start();
    }

    // ---------- HubBridge ----------

    /** The title's Log In button, or the game's start-up auto login. */
    public static int auth(final int cb, final int arg) {
        if (get("account_id") != null) {
            finishAuth(cb, arg);
        } else {
            showForm(false, new Done() {
                public void done() {
                    finishAuth(cb, arg);
                }

                public void cancelled() {
                    callback(LOGIN_CANCELLED, cb, arg);
                }
            });
        }
        return 0;
    }

    /**
     * The game treats a player as a guest while hub_guest_uid holds a guest number, so an account must never store it
     * (see java_shim.js auth). Its Hub key is account_guest instead.
     */
    private static void finishAuth(final int cb, final int arg) {
        background(new Runnable() {
            public void run() {
                try {
                    JSONObject j = hubPost("user/login", new JSONObject().put("guest_uid", get("account_guest")).put("did", did()));
                    String uid = j.optString("uid");
                    JSONObject result = j.optJSONObject("result");
                    String session = j.optString("session_key", "");
                    callback(new JSONObject()
                            .put("error_code", j.optInt("error_code", 0))
                            .put("uid", uid)
                            .put("did", result != null ? result.optString("did", did()) : did())
                            .put("sessionkey", session.length() > 0 ? session : "sk_" + uid), cb, arg);
                } catch (Exception e) {
                    fail("auth", e, cb, arg);
                }
            }
        });
    }

    public static int asyncRequest(final String name, final String params, final int cb, final int arg) {
        background(new Runnable() {
            public void run() {
                try {
                    JSONObject p = params == null || params.length() == 0 ? new JSONObject() : new JSONObject(params);
                    if ("device/registered".equals(name)) {
                        callback(new JSONObject().put("error_code", 0)
                                .put("is_registered", prop("hub_guest_uid") != null && prop("hub_logged_out") == null), cb, arg);
                    } else if ("guest/create".equals(name)) {
                        delProp("hub_logged_out");
                        JSONObject j = hubPost(name, new JSONObject().put("did", did()));
                        String guest = j.optString("guest_uid");
                        setProp("hub_guest_uid", guest);
                        // The game logs in under this did; auth reports did() on every later start too.
                        callback(new JSONObject().put("error_code", j.optInt("error_code", 0)).put("did", did()).put("guest_uid", guest), cb, arg);
                    } else if ("guest/auth".equals(name)) {
                        String guest = p.optString("guest_uid");
                        setProp("hub_guest_uid", guest);
                        JSONObject j = hubPost(name, new JSONObject().put("guest_uid", guest).put("did", did()));
                        callback(new JSONObject().put("error_code", j.optInt("error_code", 0)).put("is_auth", true), cb, arg);
                    } else {
                        if (!p.has("did")) p.put("did", did());
                        callback(hubPost(name, p), cb, arg);
                    }
                } catch (Exception e) {
                    fail(name, e, cb, arg);
                }
            }
        });
        return 0;
    }

    /** The Yes of the game's "You need to log in to use this function" prompts. Register keeps this guest's player. */
    public static int guestAcquireUid(final int cb, final int arg) {
        showForm(true, new Done() {
            public void done() {
                restart();
            }

            public void cancelled() {
                callback(LOGIN_CANCELLED, cb, arg);
            }
        });
        return 0;
    }

    public static int guestBind(String guestUid, String candidateUid, int cb, int arg) {
        callback(1, cb, arg);
        return 0;
    }

    public static int logout(int cb, int arg) {
        // An account's player must not carry over to the next guest. A plain guest keeps its did.
        if (get("account_id") != null) {
            final String token = get("token");
            prefs().edit().remove("account_id").remove("account_guest").remove("web_did").remove("token").commit();
            delProp("hub_guest_uid");
            // Ends the server sign-in too; the phone is signed out either way.
            if (token != null) {
                new Thread(new Runnable() {
                    public void run() {
                        try {
                            accountPost("/account/logout", new JSONObject().put("token", token));
                        } catch (Exception ignored) {
                            // Unreachable server: the token expires on its own.
                        }
                    }
                }).start();
            }
        }
        setProp("hub_logged_out", "1");
        callback(0, cb, arg);
        return 0;
    }

    public static int showDialog(String name, int cb, int arg) {
        callback(0, cb, arg);
        return 0;
    }

    public static int socialRequest(String a, String b, String c, int cb, int arg) {
        callback(0, cb, arg);
        return 0;
    }

    /** Relaunches the game so it logs in as the account just registered (the browser reloads the page). */
    private static void restart() {
        Activity a = activity();
        Intent launch = a.getPackageManager().getLaunchIntentForPackage(a.getPackageName());
        if (launch != null) {
            launch.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TASK);
            a.startActivity(launch);
        }
        android.os.Process.killProcess(android.os.Process.myPid());
    }

    // ---------- phone identity: runtime-permission safe ----------
    // Rewritten TelephonyManager calls land here. With a target SDK of 23+ READ_PHONE_STATE is no longer granted at
    // install, so the originals throw SecurityException; modern Android returns no hardware ids to apps anyway.

    public static String deviceId(TelephonyManager t) {
        try {
            return t.getDeviceId();
        } catch (Throwable e) {
            return null;
        }
    }

    public static String subscriberId(TelephonyManager t) {
        try {
            return t.getSubscriberId();
        } catch (Throwable e) {
            return null;
        }
    }

    public static String line1Number(TelephonyManager t) {
        try {
            return t.getLine1Number();
        } catch (Throwable e) {
            return null;
        }
    }

    public static String simSerialNumber(TelephonyManager t) {
        try {
            return t.getSimSerialNumber();
        } catch (Throwable e) {
            return null;
        }
    }

    // ---------- modern target SDK (patch_modern.py rewrites the calls; the legacy APK never reaches these) ----------
    // Target 31+ rejects a PendingIntent without FLAG_IMMUTABLE or FLAG_MUTABLE; target 34 rejects a context receiver
    // for non-system broadcasts without RECEIVER_EXPORTED or RECEIVER_NOT_EXPORTED. Behaviour is otherwise unchanged.

    private static final int FLAG_MUTABLE = 0x02000000;
    private static final int FLAG_IMMUTABLE = 0x04000000;
    private static final int RECEIVER_EXPORTED = 0x2;

    private static int pendingFlags(int flags) {
        if (Build.VERSION.SDK_INT >= 23 && (flags & (FLAG_MUTABLE | FLAG_IMMUTABLE)) == 0) flags |= FLAG_IMMUTABLE;
        return flags;
    }

    public static PendingIntent pendingActivity(Context c, int code, Intent i, int flags) {
        return PendingIntent.getActivity(c, code, i, pendingFlags(flags));
    }

    public static PendingIntent pendingActivities(Context c, int code, Intent[] i, int flags) {
        return PendingIntent.getActivities(c, code, i, pendingFlags(flags));
    }

    public static PendingIntent pendingActivities(Context c, int code, Intent[] i, int flags, Bundle options) {
        return PendingIntent.getActivities(c, code, i, pendingFlags(flags), options);
    }

    public static PendingIntent pendingBroadcast(Context c, int code, Intent i, int flags) {
        return PendingIntent.getBroadcast(c, code, i, pendingFlags(flags));
    }

    public static PendingIntent pendingService(Context c, int code, Intent i, int flags) {
        return PendingIntent.getService(c, code, i, pendingFlags(flags));
    }

    public static Intent registerReceiver(Context c, BroadcastReceiver r, IntentFilter f) {
        if (Build.VERSION.SDK_INT >= 26) return c.registerReceiver(r, f, RECEIVER_EXPORTED);
        return c.registerReceiver(r, f);
    }

    public static Intent registerReceiver(Context c, BroadcastReceiver r, IntentFilter f, String permission,
                                          Handler h) {
        if (Build.VERSION.SDK_INT >= 26) return c.registerReceiver(r, f, permission, h, RECEIVER_EXPORTED);
        return c.registerReceiver(r, f, permission, h);
    }

    // ---------- the ID/password dialog ----------

    private interface Done {
        void done();

        void cancelled();
    }

    private static void showForm(final boolean startRegister, final Done after) {
        MAIN.post(new Runnable() {
            public void run() {
                try {
                    buildForm(startRegister, after);
                } catch (Exception e) {
                    android.util.Log.w("KNS", "account dialog failed: " + e);
                    after.cancelled();
                }
            }
        });
    }

    private static EditText field(Activity a, String hint, boolean password) {
        EditText e = new EditText(a);
        e.setHint(hint);
        e.setSingleLine(true);
        e.setInputType(password ? InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD
                : InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        return e;
    }

    private static void buildForm(boolean startRegister, final Done after) {
        final Activity a = activity();
        final boolean[] registering = {startRegister};
        final boolean[] finished = {false};
        final String guest = prop("hub_guest_uid");

        int pad = (int) (16 * a.getResources().getDisplayMetrics().density);
        LinearLayout box = new LinearLayout(a);
        box.setOrientation(LinearLayout.VERTICAL);
        box.setPadding(pad, pad / 2, pad, 0);

        LinearLayout tabs = new LinearLayout(a);
        final Button tabLogin = new Button(a);
        tabLogin.setText("Log In");
        final Button tabRegister = new Button(a);
        tabRegister.setText("Register");
        tabs.addView(tabLogin, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1));
        tabs.addView(tabRegister, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1));
        box.addView(tabs);

        final EditText id = field(a, "ID", false);
        final EditText username = field(a, "In-game name", false);
        final EditText pass = field(a, "Password", true);
        final EditText again = field(a, "Repeat password", true);
        final TextView note = new TextView(a);
        final TextView error = new TextView(a);
        error.setTextColor(Color.rgb(0xd0, 0x40, 0x30));
        box.addView(id);
        box.addView(username);
        box.addView(pass);
        box.addView(again);
        box.addView(note);
        box.addView(error);

        final AlertDialog dialog = new AlertDialog.Builder(a)
                .setTitle("Knights N Squires account")
                .setView(box)
                .setPositiveButton("OK", null)
                .setNegativeButton("Cancel", null)
                .create();
        dialog.setCanceledOnTouchOutside(false);

        final Runnable show = new Runnable() {
            public void run() {
                boolean r = registering[0];
                tabLogin.setEnabled(r);
                tabRegister.setEnabled(!r);
                username.setVisibility(r ? View.VISIBLE : View.GONE);
                again.setVisibility(r ? View.VISIBLE : View.GONE);
                error.setText("");
                if (r) {
                    note.setText(guest != null ? "Your current guest player moves to this account."
                            : "ID: 3 to 24 letters, digits, _ or -. In-game name: 1 to 24 characters, unique. Password: 8 or more characters.");
                } else {
                    note.setText(guest != null ? "This phone's guest player stays behind. To keep it, use Register instead." : "");
                }
                Button ok = dialog.getButton(AlertDialog.BUTTON_POSITIVE);
                if (ok != null) ok.setText(r ? "Register" : "Log In");
            }
        };
        tabLogin.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) {
                registering[0] = false;
                show.run();
            }
        });
        tabRegister.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) {
                registering[0] = true;
                show.run();
            }
        });

        dialog.setOnDismissListener(new android.content.DialogInterface.OnDismissListener() {
            public void onDismiss(android.content.DialogInterface d) {
                if (!finished[0]) {
                    finished[0] = true;
                    after.cancelled();
                }
            }
        });
        dialog.setOnShowListener(new android.content.DialogInterface.OnShowListener() {
            public void onShow(android.content.DialogInterface d) {
                show.run();
                final Button ok = dialog.getButton(AlertDialog.BUTTON_POSITIVE);
                ok.setOnClickListener(new View.OnClickListener() {
                    public void onClick(View v) {
                        error.setText("");
                        final boolean r = registering[0];
                        if (r && !pass.getText().toString().equals(again.getText().toString())) {
                            error.setText("The passwords do not match.");
                            return;
                        }
                        ok.setEnabled(false);
                        final String idText = id.getText().toString();
                        final String passText = pass.getText().toString();
                        final String nameText = username.getText().toString();
                        background(new Runnable() {
                            public void run() {
                                try {
                                    JSONObject j;
                                    if (r) {
                                        JSONObject body = new JSONObject().put("id", idText.trim()).put("password", passText).put("nickname", nameText);
                                        if (guest != null) body.put("did", did()).put("guest_uid", guest);
                                        j = accountPost("/account/register", body);
                                        store(j, false);
                                    } else {
                                        j = accountPost("/account/login", new JSONObject().put("id", idText.trim()).put("password", passText));
                                        store(j, true);
                                    }
                                    MAIN.post(new Runnable() {
                                        public void run() {
                                            finished[0] = true;
                                            dialog.dismiss();
                                            after.done();
                                        }
                                    });
                                } catch (final Exception e) {
                                    MAIN.post(new Runnable() {
                                        public void run() {
                                            error.setText(String.valueOf(e.getMessage()));
                                            ok.setEnabled(true);
                                        }
                                    });
                                }
                            }
                        });
                    }
                });
            }
        });
        dialog.show();
        // Let the soft keyboard cover the game, not push the fullscreen surface around.
        if (dialog.getWindow() != null) {
            dialog.getWindow().clearFlags(WindowManager.LayoutParams.FLAG_ALT_FOCUSABLE_IM);
            dialog.getWindow().setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_STATE_VISIBLE);
        }
    }

    /** What the game needs for a login (account.js store). Never hub_guest_uid: that would make it treat the account as a guest. */
    private static void store(JSONObject j, boolean switchingPlayer) {
        if (switchingPlayer) forgetProps();
        prefs().edit()
                .putString("web_did", j.optString("did"))
                .putString("account_guest", j.optString("guest_uid"))
                .putString("account_id", j.optString("login_id"))
                .putString("token", j.optString("token"))
                .commit();
        delProp("hub_guest_uid");
        delProp("hub_logged_out");
    }
}
