package terminalv.smoke;

import android.app.Instrumentation;
import android.os.Bundle;
import java.lang.reflect.Method;
import java.io.File;
import org.json.JSONObject;

// Run on an isolated emulator. Obtains a login URL but never completes user login.
public final class TsnetSmoke extends Instrumentation {
    @Override public void onCreate(Bundle arguments) { super.onCreate(arguments); start(); }
    @Override public void onStart() {
        Bundle result = new Bundle();
        Object instance = null;
        Class<?> binding = null;
        try {
            android.content.Intent browser = new android.content.Intent(android.content.Intent.ACTION_VIEW,
                android.net.Uri.parse("https://login.tailscale.com/"));
            browser.addCategory(android.content.Intent.CATEGORY_BROWSABLE);
            if (getTargetContext().getPackageManager().queryIntentActivities(browser, 0).isEmpty())
                throw new AssertionError("HTTPS browser is not visible to the app");
            binding = Class.forName("tsnet.Tsnet_", true, getTargetContext().getClassLoader());
            instance = binding.getConstructor().newInstance();
            Method account = binding.getMethod("account", String.class, String.class);
            JSONObject args = new JSONObject();
            args.put("hostname", "terminalv-login-smoke");
            args.put("stateDir", new File(getTargetContext().getCacheDir(), "login-smoke-" + System.currentTimeMillis()).getAbsolutePath());
            JSONObject status = new JSONObject((String)account.invoke(instance, "begin", args.toString()));
            boolean ready = false;
            for (int i = 0; i < 45; i++) {
                String url = status.optString("authUrl");
                if (url.startsWith("https://login.tailscale.com/")) { ready = true; break; }
                Thread.sleep(1000);
                status = new JSONObject((String)account.invoke(instance, "status", "{}"));
            }
            if (!ready) throw new AssertionError("No browser login URL; state=" + status.optString("state"));
            if (status.optBoolean("running")) throw new AssertionError("Unexpected authenticated test identity");
            binding.getMethod("stop").invoke(instance);
            instance = null;
            result.putString("stream", "PASS: native account API returned a browser login URL without blocking; no user login performed.\n");
            finish(-1, result);
        } catch (Throwable error) {
            result.putString("stream", "FAIL: " + error.getClass().getSimpleName() + ": " + error.getMessage() + "\n");
            finish(1, result);
        } finally {
            if (instance != null) try { binding.getMethod("stop").invoke(instance); } catch (Throwable ignored) { }
        }
    }
}
