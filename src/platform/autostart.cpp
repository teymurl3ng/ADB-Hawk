#include "platform/autostart.hpp"

#include <fcntl.h>
#include <sys/stat.h>
#include <unistd.h>

#include <cctype>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <string_view>

namespace adbhawk::platform {

namespace {

std::string ResolveConfigHome(std::string_view override_dir) {
  if (!override_dir.empty()) {
    return std::string(override_dir);
  }
  const char* xdg = std::getenv("XDG_CONFIG_HOME");
  if (xdg != nullptr && xdg[0] == '/') {
    return std::string(xdg);
  }
  const char* home = std::getenv("HOME");
  if (home != nullptr && home[0] == '/') {
    return std::string(home) + "/.config";
  }
  return {};
}

bool EnsureDirectory0700(const std::string& dir_path) {
  if (dir_path.empty() || dir_path[0] != '/') {
    return false;
  }
  size_t pos = 1;
  while (pos <= dir_path.size()) {
    size_t slash = dir_path.find('/', pos);
    std::string prefix = (slash == std::string::npos)
                             ? dir_path
                             : dir_path.substr(0, slash);
    pos = (slash == std::string::npos) ? (dir_path.size() + 1) : (slash + 1);
    if (prefix.empty()) {
      continue;
    }
    struct stat st {};
    if (::stat(prefix.c_str(), &st) == 0) {
      if (!S_ISDIR(st.st_mode)) {
        return false;
      }
      continue;
    }
    if (::mkdir(prefix.c_str(), 0700) != 0) {
      if (::stat(prefix.c_str(), &st) != 0 || !S_ISDIR(st.st_mode)) {
        return false;
      }
    }
  }
  return true;
}

bool WriteFileAtomically0600(const std::string& dir_path,
                             const std::string& file_name,
                             std::string_view content) {
  if (!EnsureDirectory0700(dir_path)) {
    return false;
  }
  std::string final_path = dir_path + "/" + file_name;
  std::string tmp_path =
      final_path + ".tmp." + std::to_string(static_cast<long>(::getpid()));

  int fd = ::open(tmp_path.c_str(),
                  O_WRONLY | O_CREAT | O_TRUNC | O_CLOEXEC, 0600);
  if (fd < 0) {
    return false;
  }
  (void)::fchmod(fd, 0600);

  size_t off = 0;
  bool ok = true;
  while (off < content.size()) {
    ssize_t n = ::write(fd, content.data() + off, content.size() - off);
    if (n <= 0) {
      ok = false;
      break;
    }
    off += static_cast<size_t>(n);
  }
  if (ok && ::fsync(fd) != 0) {
    ok = false;
  }
  if (::close(fd) != 0) {
    ok = false;
  }
  if (!ok) {
    (void)::unlink(tmp_path.c_str());
    return false;
  }

  if (::rename(tmp_path.c_str(), final_path.c_str()) != 0) {
    (void)::unlink(tmp_path.c_str());
    return false;
  }

  int dir_fd = ::open(dir_path.c_str(), O_RDONLY | O_DIRECTORY | O_CLOEXEC);
  if (dir_fd >= 0) {
    (void)::fsync(dir_fd);
    ::close(dir_fd);
  }
  return true;
}

std::string ReadSmallFile(const std::string& path) {
  int fd = ::open(path.c_str(), O_RDONLY | O_CLOEXEC);
  if (fd < 0) {
    return {};
  }
  std::string out;
  char buf[2048];
  while (out.size() < 64 * 1024) {
    ssize_t n = ::read(fd, buf, sizeof(buf));
    if (n <= 0) {
      break;
    }
    out.append(buf, static_cast<size_t>(n));
  }
  ::close(fd);
  return out;
}

std::string_view Trim(std::string_view sv) {
  while (!sv.empty() &&
         std::isspace(static_cast<unsigned char>(sv.front())) != 0) {
    sv.remove_prefix(1);
  }
  while (!sv.empty() &&
         std::isspace(static_cast<unsigned char>(sv.back())) != 0) {
    sv.remove_suffix(1);
  }
  return sv;
}

bool EqualsIgnoreCaseAscii(std::string_view a, std::string_view b) {
  if (a.size() != b.size()) {
    return false;
  }
  for (size_t i = 0; i < a.size(); ++i) {
    if (std::tolower(static_cast<unsigned char>(a[i])) !=
        std::tolower(static_cast<unsigned char>(b[i]))) {
      return false;
    }
  }
  return true;
}

}  // namespace

std::string ResolveSelfExecutablePath() {
  char buf[4096];
  ssize_t len = ::readlink("/proc/self/exe", buf, sizeof(buf) - 1);
  if (len <= 0) {
    return {};
  }
  buf[len] = '\0';
  return std::string(buf, static_cast<size_t>(len));
}

std::string FormatDesktopExecValue(std::string_view abs_exec_path) {
  // Desktop Entry Specification: quote argument with double quotes and escape
  // `"`, `` ` ``, `$`, `\`, plus double `%` (`%%`) so field codes aren't expanded.
  std::string out;
  out.reserve(abs_exec_path.size() + 8);
  out.push_back('"');
  for (char c : abs_exec_path) {
    if (c == '\\' || c == '"' || c == '`' || c == '$') {
      out.push_back('\\');
      out.push_back(c);
    } else if (c == '%') {
      out.append("%%");
    } else {
      out.push_back(c);
    }
  }
  out.push_back('"');
  return out;
}

bool ReadAutostartEnabled(std::string_view config_home_override) {
  std::string config_home = ResolveConfigHome(config_home_override);
  if (config_home.empty()) {
    return false;
  }
  std::string path = config_home + "/autostart/adb-hawk.desktop";
  std::string content = ReadSmallFile(path);
  if (content.empty()) {
    content = ReadSmallFile(config_home + "/autostart/adb-view.desktop");
    if (content.empty()) {
      return false;
    }
  }

  bool has_exec = false;
  bool hidden_true = false;
  bool gnome_disabled = false;

  size_t pos = 0;
  while (pos < content.size()) {
    size_t eol = content.find('\n', pos);
    std::string_view line = (eol == std::string::npos)
                                ? std::string_view(content).substr(pos)
                                : std::string_view(content).substr(pos, eol - pos);
    pos = (eol == std::string::npos) ? content.size() : (eol + 1);
    line = Trim(line);
    if (line.empty() || line.front() == '#' || line.front() == '[') {
      continue;
    }
    size_t eq = line.find('=');
    if (eq == std::string_view::npos) {
      continue;
    }
    std::string_view key = Trim(line.substr(0, eq));
    std::string_view val = Trim(line.substr(eq + 1));
    if (key == "Exec" && !val.empty()) {
      has_exec = true;
    } else if (key == "Hidden" && EqualsIgnoreCaseAscii(val, "true")) {
      hidden_true = true;
    } else if (key == "X-GNOME-Autostart-enabled" &&
               EqualsIgnoreCaseAscii(val, "false")) {
      gnome_disabled = true;
    }
  }

  return has_exec && !hidden_true && !gnome_disabled;
}

bool WriteAutostartEnabled(bool enabled,
                           std::string_view abs_exec_path,
                           std::string_view config_home_override) {
  std::string config_home = ResolveConfigHome(config_home_override);
  if (config_home.empty()) {
    return false;
  }
  std::string exec_str(abs_exec_path);
  if (exec_str.empty()) {
    exec_str = ResolveSelfExecutablePath();
  }
  if (exec_str.empty() || exec_str.front() != '/') {
    return false;
  }

  std::string dir_path = config_home + "/autostart";
  std::string content = "[Desktop Entry]\n"
                        "Type=Application\n"
                        "Version=1.0\n"
                        "Name=ADB Hawk\n"
                        "Comment=Android Debug Bridge tray observer\n"
                        "Exec=" +
                        FormatDesktopExecValue(exec_str) +
                        "\n"
                        "Terminal=false\n"
                        "StartupNotify=false\n"
                        "X-GNOME-Autostart-enabled=" +
                        (enabled ? "true" : "false") +
                        "\n"
                        "Hidden=" +
                        (enabled ? "false" : "true") + "\n";

  (void)::unlink((dir_path + "/adb-view.desktop").c_str());
  return WriteFileAtomically0600(dir_path, "adb-hawk.desktop", content);
}

UserPreferences ReadUserPreferences(std::string_view config_home_override) {
  UserPreferences prefs{};
  std::string config_home = ResolveConfigHome(config_home_override);
  if (config_home.empty()) {
    return prefs;
  }
  std::string content = ReadSmallFile(config_home + "/adb-hawk/prefs.ini");
  if (content.empty()) {
    content = ReadSmallFile(config_home + "/adb-view/prefs.ini");
    if (content.empty()) {
      return prefs;
    }
  }

  size_t pos = 0;
  while (pos < content.size()) {
    size_t eol = content.find('\n', pos);
    std::string_view line = (eol == std::string::npos)
                                ? std::string_view(content).substr(pos)
                                : std::string_view(content).substr(pos, eol - pos);
    pos = (eol == std::string::npos) ? content.size() : (eol + 1);
    line = Trim(line);
    if (line.empty() || line.front() == '#' || line.front() == '[') {
      continue;
    }
    size_t eq = line.find('=');
    if (eq == std::string_view::npos) {
      continue;
    }
    std::string_view key = Trim(line.substr(0, eq));
    std::string_view val = Trim(line.substr(eq + 1));
    if (key == "theme") {
      if (EqualsIgnoreCaseAscii(val, "light")) {
        prefs.theme = ThemePreference::Light;
      } else if (EqualsIgnoreCaseAscii(val, "dark")) {
        prefs.theme = ThemePreference::Dark;
      } else {
        prefs.theme = ThemePreference::System;
      }
    } else if (key == "status_bubble") {
      prefs.show_status_bubble = EqualsIgnoreCaseAscii(val, "true");
    }
  }
  return prefs;
}

bool WriteUserPreferences(const UserPreferences& prefs,
                          std::string_view config_home_override) {
  std::string config_home = ResolveConfigHome(config_home_override);
  if (config_home.empty()) {
    return false;
  }
  std::string_view theme_str = "system";
  if (prefs.theme == ThemePreference::Light) {
    theme_str = "light";
  } else if (prefs.theme == ThemePreference::Dark) {
    theme_str = "dark";
  }
  std::string content = "[Preferences]\n"
                        "theme=" +
                        std::string(theme_str) +
                        "\n"
                        "status_bubble=" +
                        (prefs.show_status_bubble ? "true" : "false") + "\n";
  return WriteFileAtomically0600(config_home + "/adb-hawk", "prefs.ini",
                                 content);
}

}  // namespace adbhawk::platform
