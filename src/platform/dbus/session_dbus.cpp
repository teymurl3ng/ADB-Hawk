#include "platform/dbus/session_dbus.hpp"

#include <gio/gio.h>

#include <algorithm>
#include <cstdint>
#include <cstring>
#include <string>
#include <string_view>
#include <vector>

#include "core/tray_pixmap.hpp"
#include "platform/autostart.hpp"

namespace adbhawk::platform {

namespace {

constexpr const char* kInstanceXml =
    "<node>"
    "  <interface name='org.adbhawk.Instance'>"
    "    <method name='Toggle'/>"
    "  </interface>"
    "</node>";

constexpr const char* kSniXml =
    "<node>"
    "  <interface name='org.kde.StatusNotifierItem'>"
    "    <property name='Category' type='s' access='read'/>"
    "    <property name='Id' type='s' access='read'/>"
    "    <property name='Title' type='s' access='read'/>"
    "    <property name='Status' type='s' access='read'/>"
    "    <property name='WindowId' type='i' access='read'/>"
    "    <property name='IconName' type='s' access='read'/>"
    "    <property name='IconPixmap' type='a(iiay)' access='read'/>"
    "    <property name='OverlayIconName' type='s' access='read'/>"
    "    <property name='OverlayIconPixmap' type='a(iiay)' access='read'/>"
    "    <property name='AttentionIconName' type='s' access='read'/>"
    "    <property name='AttentionIconPixmap' type='a(iiay)' access='read'/>"
    "    <property name='AttentionMovieName' type='s' access='read'/>"
    "    <property name='ToolTip' type='(sa(iiay)ss)' access='read'/>"
    "    <property name='ItemIsMenu' type='b' access='read'/>"
    "    <property name='Menu' type='o' access='read'/>"
    "    <method name='ContextMenu'>"
    "      <arg type='i' name='x' direction='in'/>"
    "      <arg type='i' name='y' direction='in'/>"
    "    </method>"
    "    <method name='Activate'>"
    "      <arg type='i' name='x' direction='in'/>"
    "      <arg type='i' name='y' direction='in'/>"
    "    </method>"
    "    <method name='SecondaryActivate'>"
    "      <arg type='i' name='x' direction='in'/>"
    "      <arg type='i' name='y' direction='in'/>"
    "    </method>"
    "    <method name='Scroll'>"
    "      <arg type='i' name='delta' direction='in'/>"
    "      <arg type='s' name='orientation' direction='in'/>"
    "    </method>"
    "    <signal name='NewTitle'/>"
    "    <signal name='NewIcon'/>"
    "    <signal name='NewAttentionIcon'/>"
    "    <signal name='NewOverlayIcon'/>"
    "    <signal name='NewToolTip'/>"
    "    <signal name='NewStatus'>"
    "      <arg type='s' name='status'/>"
    "    </signal>"
    "  </interface>"
    "</node>";

constexpr const char* kDbusmenuXml =
    "<node>"
    "  <interface name='com.canonical.dbusmenu'>"
    "    <property name='Version' type='u' access='read'/>"
    "    <property name='TextDirection' type='s' access='read'/>"
    "    <property name='Status' type='s' access='read'/>"
    "    <property name='IconThemePath' type='as' access='read'/>"
    "    <method name='GetLayout'>"
    "      <arg type='i' name='parentId' direction='in'/>"
    "      <arg type='i' name='recursionDepth' direction='in'/>"
    "      <arg type='as' name='propertyNames' direction='in'/>"
    "      <arg type='u' name='revision' direction='out'/>"
    "      <arg type='(ia{sv}av)' name='layout' direction='out'/>"
    "    </method>"
    "    <method name='GetGroupProperties'>"
    "      <arg type='ai' name='ids' direction='in'/>"
    "      <arg type='as' name='propertyNames' direction='in'/>"
    "      <arg type='a(ia{sv})' name='properties' direction='out'/>"
    "    </method>"
    "    <method name='GetProperty'>"
    "      <arg type='i' name='id' direction='in'/>"
    "      <arg type='s' name='name' direction='in'/>"
    "      <arg type='v' name='value' direction='out'/>"
    "    </method>"
    "    <method name='Event'>"
    "      <arg type='i' name='id' direction='in'/>"
    "      <arg type='s' name='eventId' direction='in'/>"
    "      <arg type='v' name='data' direction='in'/>"
    "      <arg type='u' name='timestamp' direction='in'/>"
    "    </method>"
    "    <method name='EventGroup'>"
    "      <arg type='a(isvu)' name='events' direction='in'/>"
    "      <arg type='ai' name='idErrors' direction='out'/>"
    "    </method>"
    "    <method name='AboutToShow'>"
    "      <arg type='i' name='id' direction='in'/>"
    "      <arg type='b' name='needUpdate' direction='out'/>"
    "    </method>"
    "    <signal name='ItemsPropertiesUpdated'>"
    "      <arg type='a(ia{sv})' name='updatedProps'/>"
    "      <arg type='a(ias)' name='removedProps'/>"
    "    </signal>"
    "    <signal name='LayoutUpdated'>"
    "      <arg type='u' name='revision'/>"
    "      <arg type='i' name='parent'/>"
    "    </signal>"
    "    <signal name='ItemActivationRequested'>"
    "      <arg type='i' name='id'/>"
    "      <arg type='u' name='timestamp'/>"
    "    </signal>"
    "  </interface>"
    "</node>";

GVariant* BuildIconPixmapVariant(const std::vector<core::TrayPixmap>& pixmaps) {
  GVariantBuilder b;
  g_variant_builder_init(&b, G_VARIANT_TYPE("a(iiay)"));
  for (const auto& px : pixmaps) {
    g_variant_builder_open(&b, G_VARIANT_TYPE("(iiay)"));
    g_variant_builder_add(&b, "i", static_cast<gint>(px.width));
    g_variant_builder_add(&b, "i", static_cast<gint>(px.height));
    g_variant_builder_open(&b, G_VARIANT_TYPE("ay"));
    for (uint8_t byte : px.argb_network_bytes) {
      g_variant_builder_add(&b, "y", byte);
    }
    g_variant_builder_close(&b);
    g_variant_builder_close(&b);
  }
  return g_variant_builder_end(&b);
}
}  // namespace

bool SessionDbusManager::ReadAutomountSuppressed() {
  GSettingsSchemaSource* src = g_settings_schema_source_get_default();
  if (src == nullptr) return false;
  const char* const schemas[] = {
      "org.cinnamon.desktop.media-handling",
      "org.gnome.desktop.media-handling",
  };
  for (const char* schema_name : schemas) {
    GSettingsSchema* schema = g_settings_schema_source_lookup(src, schema_name, TRUE);
    if (schema != nullptr) {
      GSettings* s = g_settings_new(schema_name);
      gboolean open = g_settings_get_boolean(s, "automount-open");
      g_object_unref(s);
      g_settings_schema_unref(schema);
      return !open;
    }
  }
  return false;
}

void SessionDbusManager::WriteAutomountSuppressed(bool suppress) {
  GSettingsSchemaSource* src = g_settings_schema_source_get_default();
  if (src == nullptr) return;
  const char* const schemas[] = {
      "org.cinnamon.desktop.media-handling",
      "org.gnome.desktop.media-handling",
  };
  for (const char* schema_name : schemas) {
    GSettingsSchema* schema = g_settings_schema_source_lookup(src, schema_name, TRUE);
    if (schema != nullptr) {
      GSettings* s = g_settings_new(schema_name);
      g_settings_set_boolean(s, "automount-open", !suppress);
      g_object_unref(s);
      g_settings_schema_unref(schema);
    }
  }
}


struct SessionDbusManager::Impl {
  DbusMenuCallbacks menu_cb;
  DbusEventCallbacks event_cb;

  GDBusConnection* session_conn = nullptr;
  GDBusConnection* system_conn = nullptr;
  bool owns_session_conn = false;
  bool owns_system_conn = false;

  guint instance_owner_id = 0;
  guint instance_reg_id = 0;
  guint sni_reg_id = 0;
  guint dbusmenu_reg_id = 0;
  guint watcher_sub_id = 0;
  guint screensaver_sub_id = 0;
  guint login1_sub_id = 0;
  guint portal_sub_id = 0;

  bool has_sni_host = false;
  core::TrayStatusKind current_tray_status = core::TrayStatusKind::Idle;
  std::string current_tooltip = "ADB Hawk";
  std::vector<core::TrayPixmap> current_pixmaps;

  UserPreferences current_prefs{};
  bool current_autostart = false;
  bool current_suppress_automount = false;
  uint32_t menu_revision = 1;
  bool supports_notification_actions = false;

  static void OnInstanceMethodCall(GDBusConnection*,
                                   const gchar*,
                                   const gchar*,
                                   const gchar*,
                                   const gchar* method_name,
                                   GVariant*,
                                   GDBusMethodInvocation* invocation,
                                   gpointer user_data) {
    auto* self = static_cast<Impl*>(user_data);
    if (std::strcmp(method_name, "Toggle") == 0) {
      if (self->event_cb.on_instance_toggle_received) {
        self->event_cb.on_instance_toggle_received();
      }
      g_dbus_method_invocation_return_value(invocation, g_variant_new("()"));
    } else {
      g_dbus_method_invocation_return_error(
          invocation, G_DBUS_ERROR, G_DBUS_ERROR_UNKNOWN_METHOD,
          "Unknown method %s", method_name);
    }
  }

  static void OnSniMethodCall(GDBusConnection*,
                              const gchar*,
                              const gchar*,
                              const gchar*,
                              const gchar* method_name,
                              GVariant* parameters,
                              GDBusMethodInvocation* invocation,
                              gpointer user_data) {
    auto* self = static_cast<Impl*>(user_data);
    if (std::strcmp(method_name, "Activate") == 0 ||
        std::strcmp(method_name, "SecondaryActivate") == 0) {
      gint32 x = 0;
      gint32 y = 0;
      if (parameters != nullptr) {
        g_variant_get(parameters, "(ii)", &x, &y);
      }
      if (self->event_cb.on_tray_activate) {
        self->event_cb.on_tray_activate(x, y);
      }
      g_dbus_method_invocation_return_value(invocation, g_variant_new("()"));
    } else if (std::strcmp(method_name, "ContextMenu") == 0) {
      gint32 x = 0;
      gint32 y = 0;
      if (parameters != nullptr) {
        g_variant_get(parameters, "(ii)", &x, &y);
      }
      if (self->event_cb.on_tray_activate) {
        self->event_cb.on_tray_activate(x, y);
      }
      g_dbus_method_invocation_return_value(invocation, g_variant_new("()"));
    } else if (std::strcmp(method_name, "Scroll") == 0) {
      g_dbus_method_invocation_return_value(invocation, g_variant_new("()"));
    } else {
      g_dbus_method_invocation_return_error(
          invocation, G_DBUS_ERROR, G_DBUS_ERROR_UNKNOWN_METHOD,
          "Unknown method %s", method_name);
    }
  }

  static GVariant* OnSniGetProperty(GDBusConnection*,
                                    const gchar*,
                                    const gchar*,
                                    const gchar*,
                                    const gchar* property_name,
                                    GError**,
                                    gpointer user_data) {
    auto* self = static_cast<Impl*>(user_data);
    if (std::strcmp(property_name, "Category") == 0) {
      return g_variant_new_string("Hardware");
    }
    if (std::strcmp(property_name, "Id") == 0) {
      return g_variant_new_string("adb-hawk");
    }
    if (std::strcmp(property_name, "Title") == 0) {
      return g_variant_new_string("ADB Hawk");
    }
    if (std::strcmp(property_name, "Status") == 0) {
      // D5: Status=Active always. KDE hides Passive icons in overflow.
      return g_variant_new_string("Active");
    }
    if (std::strcmp(property_name, "WindowId") == 0) {
      return g_variant_new_int32(0);
    }
    if (std::strcmp(property_name, "IconName") == 0) {
      return g_variant_new_string("");
    }
    if (std::strcmp(property_name, "IconPixmap") == 0) {
      return BuildIconPixmapVariant(self->current_pixmaps);
    }
    if (std::strcmp(property_name, "OverlayIconName") == 0 ||
        std::strcmp(property_name, "AttentionIconName") == 0 ||
        std::strcmp(property_name, "AttentionMovieName") == 0) {
      return g_variant_new_string("");
    }
    if (std::strcmp(property_name, "OverlayIconPixmap") == 0 ||
        std::strcmp(property_name, "AttentionIconPixmap") == 0) {
      GVariantBuilder b;
      g_variant_builder_init(&b, G_VARIANT_TYPE("a(iiay)"));
      return g_variant_builder_end(&b);
    }
    if (std::strcmp(property_name, "ItemIsMenu") == 0) {
      return g_variant_new_boolean(FALSE);
    }
    if (std::strcmp(property_name, "Menu") == 0) {
      return g_variant_new_object_path("/MenuBar");
    }
    if (std::strcmp(property_name, "ToolTip") == 0) {
      GVariantBuilder px_b;
      g_variant_builder_init(&px_b, G_VARIANT_TYPE("a(iiay)"));
      return g_variant_new("(s@a(iiay)ss)", self->current_tooltip.c_str(),
                           g_variant_builder_end(&px_b), "", "");
    }
    return nullptr;
  }

  GVariant* BuildDbusmenuProperties(int id) {
    GVariantBuilder props;
    g_variant_builder_init(&props, G_VARIANT_TYPE("a{sv}"));

    switch (id) {
      case 0:  // root
        g_variant_builder_add(&props, "{sv}", "children-display",
                              g_variant_new_string("submenu"));
        break;
      case 1:  // Open ADB Hawk
        g_variant_builder_add(&props, "{sv}", "label",
                              g_variant_new_string("Open ADB Hawk"));
        g_variant_builder_add(&props, "{sv}", "enabled",
                              g_variant_new_boolean(TRUE));
        break;
      case 2:  // Pair with Wi-Fi
        g_variant_builder_add(&props, "{sv}", "label",
                              g_variant_new_string("Pair device with Wi-Fi…"));
        g_variant_builder_add(&props, "{sv}", "enabled",
                              g_variant_new_boolean(TRUE));
        break;
      case 3:  // Show status bubble
        g_variant_builder_add(&props, "{sv}", "label",
                              g_variant_new_string("Show status bubble"));
        g_variant_builder_add(&props, "{sv}", "toggle-type",
                              g_variant_new_string("checkmark"));
        g_variant_builder_add(
            &props, "{sv}", "toggle-state",
            g_variant_new_int32(current_prefs.show_status_bubble ? 1 : 0));
        break;
      case 4:  // Theme
        g_variant_builder_add(&props, "{sv}", "label",
                              g_variant_new_string("Theme"));
        g_variant_builder_add(&props, "{sv}", "children-display",
                              g_variant_new_string("submenu"));
        break;
      case 41:  // System theme
        g_variant_builder_add(&props, "{sv}", "label",
                              g_variant_new_string("Use system setting"));
        g_variant_builder_add(&props, "{sv}", "toggle-type",
                              g_variant_new_string("radio"));
        g_variant_builder_add(
            &props, "{sv}", "toggle-state",
            g_variant_new_int32(
                current_prefs.theme == ThemePreference::System ? 1 : 0));
        break;
      case 42:  // Light theme
        g_variant_builder_add(&props, "{sv}", "label",
                              g_variant_new_string("Light"));
        g_variant_builder_add(&props, "{sv}", "toggle-type",
                              g_variant_new_string("radio"));
        g_variant_builder_add(
            &props, "{sv}", "toggle-state",
            g_variant_new_int32(
                current_prefs.theme == ThemePreference::Light ? 1 : 0));
        break;
      case 43:  // Dark theme
        g_variant_builder_add(&props, "{sv}", "label",
                              g_variant_new_string("Dark"));
        g_variant_builder_add(&props, "{sv}", "toggle-type",
                              g_variant_new_string("radio"));
        g_variant_builder_add(
            &props, "{sv}", "toggle-state",
            g_variant_new_int32(
                current_prefs.theme == ThemePreference::Dark ? 1 : 0));
        break;
      case 5:  // Run at login
        g_variant_builder_add(&props, "{sv}", "label",
                              g_variant_new_string("Run at login"));
        g_variant_builder_add(&props, "{sv}", "toggle-type",
                              g_variant_new_string("checkmark"));
        g_variant_builder_add(&props, "{sv}", "toggle-state",
                              g_variant_new_int32(current_autostart ? 1 : 0));
        break;
      case 8:  // Prevent opening folder on connect
        g_variant_builder_add(&props, "{sv}", "label",
                              g_variant_new_string("Prevent opening folder on connect"));
        g_variant_builder_add(&props, "{sv}", "toggle-type",
                              g_variant_new_string("checkmark"));
        g_variant_builder_add(
            &props, "{sv}", "toggle-state",
            g_variant_new_int32(current_suppress_automount ? 1 : 0));
        break;
      case 6:  // Separator
        g_variant_builder_add(&props, "{sv}", "type",
                              g_variant_new_string("separator"));
        break;
      case 7:  // Quit
        g_variant_builder_add(&props, "{sv}", "label",
                              g_variant_new_string("Quit"));
        g_variant_builder_add(&props, "{sv}", "enabled",
                              g_variant_new_boolean(TRUE));
        break;
    }
    return g_variant_builder_end(&props);
  }

  GVariant* BuildDbusmenuNode(int id) {
    GVariant* props = BuildDbusmenuProperties(id);
    GVariantBuilder children;
    g_variant_builder_init(&children, G_VARIANT_TYPE("av"));

    if (id == 0) {
      g_variant_builder_add(&children, "v", BuildDbusmenuNode(1));
      g_variant_builder_add(&children, "v", BuildDbusmenuNode(2));
      g_variant_builder_add(&children, "v", BuildDbusmenuNode(3));
      g_variant_builder_add(&children, "v", BuildDbusmenuNode(4));
      g_variant_builder_add(&children, "v", BuildDbusmenuNode(5));
      g_variant_builder_add(&children, "v", BuildDbusmenuNode(8));
      g_variant_builder_add(&children, "v", BuildDbusmenuNode(6));
      g_variant_builder_add(&children, "v", BuildDbusmenuNode(7));
    } else if (id == 4) {
      g_variant_builder_add(&children, "v", BuildDbusmenuNode(41));
      g_variant_builder_add(&children, "v", BuildDbusmenuNode(42));
      g_variant_builder_add(&children, "v", BuildDbusmenuNode(43));
    }

    return g_variant_new("(i@a{sv}@av)", id, props,
                         g_variant_builder_end(&children));
  }

  void DispatchMenuAction(int id) {
    switch (id) {
      case 1:
        if (menu_cb.on_open_flyout) menu_cb.on_open_flyout();
        break;
      case 2:
        if (menu_cb.on_pair_qr) menu_cb.on_pair_qr();
        break;
      case 3:
        current_prefs.show_status_bubble = !current_prefs.show_status_bubble;
        WriteUserPreferences(current_prefs);
        if (menu_cb.on_toggle_bubble) {
          menu_cb.on_toggle_bubble(current_prefs.show_status_bubble);
        }
        EmitLayoutUpdated();
        break;
      case 41:
        current_prefs.theme = ThemePreference::System;
        WriteUserPreferences(current_prefs);
        if (menu_cb.on_select_theme) {
          menu_cb.on_select_theme(ThemePreference::System);
        }
        EmitLayoutUpdated();
        break;
      case 42:
        current_prefs.theme = ThemePreference::Light;
        WriteUserPreferences(current_prefs);
        if (menu_cb.on_select_theme) {
          menu_cb.on_select_theme(ThemePreference::Light);
        }
        EmitLayoutUpdated();
        break;
      case 43:
        current_prefs.theme = ThemePreference::Dark;
        WriteUserPreferences(current_prefs);
        if (menu_cb.on_select_theme) {
          menu_cb.on_select_theme(ThemePreference::Dark);
        }
        EmitLayoutUpdated();
        break;
      case 5:
        current_autostart = !current_autostart;
        WriteAutostartEnabled(current_autostart);
        if (menu_cb.on_toggle_autostart) {
          menu_cb.on_toggle_autostart(current_autostart);
        }
        EmitLayoutUpdated();
        break;
      case 8:
        current_suppress_automount = !current_suppress_automount;
        WriteAutomountSuppressed(current_suppress_automount);
        if (menu_cb.on_toggle_suppress_automount) {
          menu_cb.on_toggle_suppress_automount(current_suppress_automount);
        }
        EmitLayoutUpdated();
        break;
      case 7:
        if (menu_cb.on_quit) menu_cb.on_quit();
        break;
      default:
        break;
    }
  }

  void EmitLayoutUpdated() {
    menu_revision++;
    if (session_conn != nullptr) {
      g_dbus_connection_emit_signal(
          session_conn, nullptr, "/MenuBar",
          "com.canonical.dbusmenu", "LayoutUpdated",
          g_variant_new("(ui)", menu_revision, 0), nullptr);
    }
  }

  static void OnDbusmenuMethodCall(GDBusConnection*,
                                   const gchar*,
                                   const gchar*,
                                   const gchar*,
                                   const gchar* method_name,
                                   GVariant* parameters,
                                   GDBusMethodInvocation* invocation,
                                   gpointer user_data) {
    auto* self = static_cast<Impl*>(user_data);

    if (std::strcmp(method_name, "GetLayout") == 0) {
      gint32 parent_id = 0;
      gint32 depth = 0;
      g_variant_get(parameters, "(ii^as)", &parent_id, &depth, nullptr);
      GVariant* node = self->BuildDbusmenuNode(parent_id);
      g_dbus_method_invocation_return_value(
          invocation, g_variant_new("(u@(ia{sv}av))", self->menu_revision, node));
    } else if (std::strcmp(method_name, "GetGroupProperties") == 0) {
      GVariant* ids_var = g_variant_get_child_value(parameters, 0);
      std::vector<int> target_ids;
      if (ids_var != nullptr) {
        GVariantIter iter;
        g_variant_iter_init(&iter, ids_var);
        gint32 item_id = 0;
        while (g_variant_iter_next(&iter, "i", &item_id)) {
          target_ids.push_back(item_id);
        }
        g_variant_unref(ids_var);
      }
      if (target_ids.empty()) {
        target_ids = {0, 1, 2, 3, 4, 41, 42, 43, 5, 8, 6, 7};
      }
      GVariantBuilder b;
      g_variant_builder_init(&b, G_VARIANT_TYPE("a(ia{sv})"));
      for (int tid : target_ids) {
        GVariant* props = self->BuildDbusmenuProperties(tid);
        g_variant_builder_add(&b, "(i@a{sv})", tid, props);
      }
      g_dbus_method_invocation_return_value(
          invocation, g_variant_new("(a(ia{sv}))", &b));
    } else if (std::strcmp(method_name, "GetProperty") == 0) {
      gint32 id = 0;
      const gchar* prop_name = nullptr;
      g_variant_get(parameters, "(is)", &id, &prop_name);
      GVariant* props = self->BuildDbusmenuProperties(id);
      GVariant* val = g_variant_lookup_value(props, prop_name, nullptr);
      g_variant_unref(props);
      if (val != nullptr) {
        g_dbus_method_invocation_return_value(invocation, g_variant_new("(v)", val));
        g_variant_unref(val);
      } else {
        g_dbus_method_invocation_return_value(
            invocation, g_variant_new("(v)", g_variant_new_string("")));
      }
    } else if (std::strcmp(method_name, "AboutToShow") == 0) {
      // Re-read autostart, automount, and user preferences on AboutToShow per F2
      self->current_prefs = ReadUserPreferences();
      self->current_autostart = ReadAutostartEnabled();
      self->current_suppress_automount = ReadAutomountSuppressed();
      g_dbus_method_invocation_return_value(invocation,
                                            g_variant_new("(b)", TRUE));
    } else if (std::strcmp(method_name, "Event") == 0) {
      gint32 id = 0;
      const gchar* ev_id = nullptr;
      g_variant_get(parameters, "(is@vu)", &id, &ev_id, nullptr, nullptr);
      if (ev_id != nullptr && std::strcmp(ev_id, "clicked") == 0) {
        self->DispatchMenuAction(id);
      }
      g_dbus_method_invocation_return_value(invocation, g_variant_new("()"));
    } else if (std::strcmp(method_name, "EventGroup") == 0) {
      GVariantIter* iter = nullptr;
      g_variant_get(parameters, "(a(isvu))", &iter);
      GVariantBuilder err_b;
      g_variant_builder_init(&err_b, G_VARIANT_TYPE("ai"));
      if (iter != nullptr) {
        gint32 id = 0;
        const gchar* ev_id = nullptr;
        GVariant* data = nullptr;
        guint32 timestamp = 0;
        while (g_variant_iter_next(iter, "(isvu)", &id, &ev_id, &data, &timestamp)) {
          if (ev_id != nullptr && std::strcmp(ev_id, "clicked") == 0) {
            self->DispatchMenuAction(id);
          }
          if (data != nullptr) {
            g_variant_unref(data);
          }
        }
        g_variant_iter_free(iter);
      }
      g_dbus_method_invocation_return_value(invocation,
                                            g_variant_new("(ai)", &err_b));
    } else {
      g_dbus_method_invocation_return_value(invocation, g_variant_new("()"));
    }
  }

  static GVariant* OnDbusmenuGetProperty(GDBusConnection*,
                                        const gchar*,
                                        const gchar*,
                                        const gchar*,
                                        const gchar* property_name,
                                        GError**,
                                        gpointer) {
    if (std::strcmp(property_name, "Version") == 0) {
      return g_variant_new_uint32(3);
    }
    if (std::strcmp(property_name, "TextDirection") == 0) {
      return g_variant_new_string("ltr");
    }
    if (std::strcmp(property_name, "Status") == 0) {
      return g_variant_new_string("normal");
    }
    if (std::strcmp(property_name, "IconThemePath") == 0) {
      GVariantBuilder b;
      g_variant_builder_init(&b, G_VARIANT_TYPE("as"));
      return g_variant_builder_end(&b);
    }
    return nullptr;
  }

  void RegisterSniWithWatcher() {
    if (session_conn == nullptr) {
      return;
    }
    g_dbus_connection_call(
        session_conn, "org.kde.StatusNotifierWatcher",
        "/StatusNotifierWatcher", "org.kde.StatusNotifierWatcher",
        "RegisterStatusNotifierItem",
        g_variant_new("(s)", "/StatusNotifierItem"), nullptr,
        G_DBUS_CALL_FLAGS_NONE, 1000, nullptr, nullptr, nullptr);
  }
};

SessionDbusManager::SessionDbusManager(DbusMenuCallbacks menu_cb,
                                       DbusEventCallbacks event_cb)
    : impl_(std::make_unique<Impl>()) {
  impl_->menu_cb = std::move(menu_cb);
  impl_->event_cb = std::move(event_cb);
  impl_->current_prefs = ReadUserPreferences();
  impl_->current_autostart = ReadAutostartEnabled();
  impl_->current_suppress_automount = ReadAutomountSuppressed();
  impl_->current_pixmaps =
      core::RenderStandardTrayPixmaps(core::TrayStatusKind::Idle);
}

SessionDbusManager::~SessionDbusManager() { Stop(); }

SingleInstanceStatus SessionDbusManager::Initialize(
    GDBusConnection* injected_session_bus,
    GDBusConnection* injected_system_bus) {
  if (injected_session_bus != nullptr) {
    impl_->session_conn = injected_session_bus;
    impl_->owns_session_conn = false;
  } else {
    GError* err = nullptr;
    impl_->session_conn = g_bus_get_sync(G_BUS_TYPE_SESSION, nullptr, &err);
    if (impl_->session_conn == nullptr) {
      if (err != nullptr) g_error_free(err);
      return SingleInstanceStatus::Failed;
    }
    impl_->owns_session_conn = true;
  }

  if (injected_system_bus != nullptr) {
    impl_->system_conn = injected_system_bus;
    impl_->owns_system_conn = false;
  }

  // 1. Export `org.adbhawk.Instance`
  GError* parse_err = nullptr;
  GDBusNodeInfo* inst_node =
      g_dbus_node_info_new_for_xml(kInstanceXml, &parse_err);
  if (inst_node != nullptr) {
    static const GDBusInterfaceVTable inst_vtable = {
        Impl::OnInstanceMethodCall, nullptr, nullptr, {nullptr}};
    impl_->instance_reg_id = g_dbus_connection_register_object(
        impl_->session_conn, "/org/adbhawk/Instance", inst_node->interfaces[0],
        &inst_vtable, impl_.get(), nullptr, nullptr);
    g_dbus_node_info_unref(inst_node);
  }

  // 2. Export StatusNotifierItem
  GDBusNodeInfo* sni_node = g_dbus_node_info_new_for_xml(kSniXml, nullptr);
  if (sni_node != nullptr) {
    static const GDBusInterfaceVTable sni_vtable = {
        Impl::OnSniMethodCall, Impl::OnSniGetProperty, nullptr, {nullptr}};
    impl_->sni_reg_id = g_dbus_connection_register_object(
        impl_->session_conn, "/StatusNotifierItem", sni_node->interfaces[0],
        &sni_vtable, impl_.get(), nullptr, nullptr);
    g_dbus_node_info_unref(sni_node);
  }

  // 3. Export DBusMenu
  GDBusNodeInfo* menu_node =
      g_dbus_node_info_new_for_xml(kDbusmenuXml, nullptr);
  if (menu_node != nullptr) {
    static const GDBusInterfaceVTable menu_vtable = {
        Impl::OnDbusmenuMethodCall, Impl::OnDbusmenuGetProperty, nullptr,
        {nullptr}};
    impl_->dbusmenu_reg_id = g_dbus_connection_register_object(
        impl_->session_conn, "/MenuBar", menu_node->interfaces[0],
        &menu_vtable, impl_.get(), nullptr, nullptr);
    g_dbus_node_info_unref(menu_node);
  }

  // Register with StatusNotifierWatcher
  impl_->RegisterSniWithWatcher();

  return SingleInstanceStatus::AcquiredPrimary;
}

void SessionDbusManager::Stop() {
  if (impl_->session_conn != nullptr) {
    if (impl_->instance_reg_id > 0) {
      g_dbus_connection_unregister_object(impl_->session_conn,
                                          impl_->instance_reg_id);
      impl_->instance_reg_id = 0;
    }
    if (impl_->sni_reg_id > 0) {
      g_dbus_connection_unregister_object(impl_->session_conn,
                                          impl_->sni_reg_id);
      impl_->sni_reg_id = 0;
    }
    if (impl_->dbusmenu_reg_id > 0) {
      g_dbus_connection_unregister_object(impl_->session_conn,
                                          impl_->dbusmenu_reg_id);
      impl_->dbusmenu_reg_id = 0;
    }
    if (impl_->owns_session_conn) {
      g_object_unref(impl_->session_conn);
      impl_->session_conn = nullptr;
    }
  }
}

void SessionDbusManager::UpdateTrayFromSnapshot(
    const core::AppSnapshot& snapshot,
    bool is_dark_panel) {
  (void)is_dark_panel;
  impl_->current_tray_status = snapshot.health.tray;
  impl_->current_tooltip = snapshot.health.tray_tooltip;
  impl_->current_pixmaps =
      core::RenderStandardTrayPixmaps(snapshot.health.tray);

  if (impl_->session_conn != nullptr) {
    (void)g_dbus_connection_emit_signal(
        impl_->session_conn, nullptr, "/StatusNotifierItem",
        "org.kde.StatusNotifierItem", "NewIcon", nullptr, nullptr);
    (void)g_dbus_connection_emit_signal(
        impl_->session_conn, nullptr, "/StatusNotifierItem",
        "org.kde.StatusNotifierItem", "NewToolTip", nullptr, nullptr);
  }
}

void SessionDbusManager::UpdateMenuState(const UserPreferences& prefs,
                                        bool autostart_enabled) {
  impl_->current_prefs = prefs;
  impl_->current_autostart = autostart_enabled;
  impl_->menu_revision += 1;

  if (impl_->session_conn != nullptr) {
    (void)g_dbus_connection_emit_signal(
        impl_->session_conn, nullptr, "/MenuBar", "com.canonical.dbusmenu",
        "LayoutUpdated", g_variant_new("(ui)", impl_->menu_revision, 0),
        nullptr);
  }
}

uint32_t SessionDbusManager::SendNotification(
    std::string_view title,
    std::string_view body,
    uint32_t replaces_id,
    bool urgency_critical,
    std::optional<std::string_view> action_label) {
  if (impl_->session_conn == nullptr) {
    return 0;
  }

  std::string title_str(title);
  std::string body_str(body);
  std::string action_str =
      action_label.has_value() ? std::string(*action_label) : "";

  GVariantBuilder actions_b;
  g_variant_builder_init(&actions_b, G_VARIANT_TYPE("as"));
  if (action_label.has_value()) {
    g_variant_builder_add(&actions_b, "s", "default");
    g_variant_builder_add(&actions_b, "s", action_str.c_str());
  }

  GVariantBuilder hints_b;
  g_variant_builder_init(&hints_b, G_VARIANT_TYPE("a{sv}"));
  g_variant_builder_add(&hints_b, "{sv}", "transient",
                        g_variant_new_boolean(TRUE));
  g_variant_builder_add(&hints_b, "{sv}", "desktop-entry",
                        g_variant_new_string("adb-hawk"));
  g_variant_builder_add(
      &hints_b, "{sv}", "urgency",
      g_variant_new_byte(static_cast<guchar>(urgency_critical ? 2 : 1)));

  GVariant* params = g_variant_new(
      "(susss@as@a{sv}i)", "ADB Hawk", static_cast<guint32>(replaces_id),
      "", title_str.c_str(), body_str.c_str(),
      g_variant_builder_end(&actions_b), g_variant_builder_end(&hints_b), -1);

  GError* err = nullptr;
  GVariant* reply = g_dbus_connection_call_sync(
      impl_->session_conn, "org.freedesktop.Notifications",
      "/org/freedesktop/Notifications", "org.freedesktop.Notifications",
      "Notify", params, G_VARIANT_TYPE("(u)"), G_DBUS_CALL_FLAGS_NONE, 1000,
      nullptr, &err);

  uint32_t assigned_id = replaces_id;
  if (reply != nullptr) {
    guint32 res_id = 0;
    g_variant_get(reply, "(u)", &res_id);
    assigned_id = res_id;
    g_variant_unref(reply);
  }
  if (err != nullptr) {
    g_error_free(err);
  }
  return assigned_id;
}

bool SessionDbusManager::HasStatusNotifierHost() const {
  return impl_->has_sni_host;
}

}  // namespace adbhawk::platform
