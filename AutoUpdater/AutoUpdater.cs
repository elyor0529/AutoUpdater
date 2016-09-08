using System;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Cache;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using Microsoft.Win32;

namespace AutoUpdater
{
    /// <summary>
    ///     Main class that lets you auto update applications by setting some static fields and executing its Start method.
    /// </summary>
    public static class AutoUpdater
    {
        /// <summary>
        ///     A delegate type for hooking up update notifications.
        /// </summary>
        /// <param name="args">
        ///     An object containing all the parameters recieved from AppCast XML file. If there will be an error
        ///     while looking for the XML file then this object will be null.
        /// </param>
        public delegate void CheckForUpdateEventHandler(UpdateInfoEventArgs args);

        internal static string DialogTitle;

        internal static string ChangeLogUrl;

        internal static string DownloadUrl;

        internal static string RegistryLocation;

        internal static string AppTitle;

        internal static Version CurrentVersion;

        internal static Version InstalledVersion;

        internal static bool IsWinFormsApplication;

        /// <summary>
        ///     URL of the xml file that contains information about latest version of the application.
        /// </summary>
        public static string AppCastUrl;

        /// <summary>
        ///     Opens the download url in default browser if true. Very usefull if you have portable application.
        /// </summary>
        public static bool OpenDownloadPage;

        /// <summary>
        ///     Sets the current culture of the auto update notification window. Set this value if your application supports
        ///     functionalty to change the languge of the application.
        /// </summary>
        public static CultureInfo CurrentCulture;

        /// <summary>
        ///     If this is true users see dialog where they can set remind later interval otherwise it will take the interval from
        ///     RemindLaterAt and RemindLaterTimeSpan fields.
        /// </summary>
        public static bool LetUserSelectRemindLater = true;

        /// <summary>
        ///     Remind Later interval after user should be reminded of update.
        /// </summary>
        public static int RemindLaterAt = 2;

        /// <summary>
        ///     Set if RemindLaterAt interval should be in Minutes, Hours or Days.
        /// </summary>
        public static RemindLaterFormat RemindLaterTimeSpan = RemindLaterFormat.Days;

        /// <summary>
        ///     An event that clients can use to be notified whenever the update is checked.
        /// </summary>
        public static event CheckForUpdateEventHandler CheckForUpdateEvent;

        /// <summary>
        ///     Start checking for new version of application and display dialog to the user if update is available.
        /// </summary>
        public static void Start()
        {
            Start(AppCastUrl);
        }

        /// <summary>
        ///     Start checking for new version of application and display dialog to the user if update is available.
        /// </summary>
        /// <param name="appCast">URL of the xml file that contains information about latest version of the application.</param>
        public static void Start(string appCast)
        {
            AppCastUrl = appCast;

            IsWinFormsApplication = Application.MessageLoop;

            var backgroundWorker = new BackgroundWorker();

            backgroundWorker.DoWork += BackgroundWorkerDoWork;

            backgroundWorker.RunWorkerAsync();
        }

        private static void BackgroundWorkerDoWork(object sender, DoWorkEventArgs e)
        {
            var mainAssembly = Assembly.GetEntryAssembly();
            var companyAttribute =
                (AssemblyCompanyAttribute) GetAttribute(mainAssembly, typeof(AssemblyCompanyAttribute));
            var titleAttribute = (AssemblyTitleAttribute) GetAttribute(mainAssembly, typeof(AssemblyTitleAttribute));
            AppTitle = titleAttribute != null ? titleAttribute.Title : mainAssembly.GetName().Name;
            var appCompany = companyAttribute != null ? companyAttribute.Company : "";

            RegistryLocation = !string.IsNullOrEmpty(appCompany)
                ? string.Format(@"Software\{0}\{1}\AutoUpdater", appCompany, AppTitle)
                : string.Format(@"Software\{0}\AutoUpdater", AppTitle);

            var updateKey = Registry.CurrentUser.OpenSubKey(RegistryLocation);

            var remindLaterTime = updateKey?.GetValue("remindlater");

            if (remindLaterTime != null)
            {
                var remindLater = Convert.ToDateTime(remindLaterTime.ToString(),
                    CultureInfo.CreateSpecificCulture("en-US"));

                var compareResult = DateTime.Compare(DateTime.Now, remindLater);

                if (compareResult < 0)
                {
                    var updateForm = new UpdateForm(true);
                    updateForm.SetTimer(remindLater);
                    return;
                }
            }

            InstalledVersion = mainAssembly.GetName().Version;

            var webRequest = WebRequest.Create(AppCastUrl);
            webRequest.CachePolicy = new HttpRequestCachePolicy(HttpRequestCacheLevel.NoCacheNoStore);

            WebResponse webResponse;

            try
            {
                webResponse = webRequest.GetResponse();
            }
            catch (Exception)
            {
                CheckForUpdateEvent?.Invoke(null);
                return;
            }

            var appCastStream = webResponse.GetResponseStream();

            var receivedAppCastDocument = new XmlDocument();

            if (appCastStream != null)
            {
                receivedAppCastDocument.Load(appCastStream);
            }
            else
            {
                CheckForUpdateEvent?.Invoke(null);
                return;
            }

            var appCastItems = receivedAppCastDocument.SelectNodes("item");

            if (appCastItems != null)
                foreach (XmlNode item in appCastItems)
                {
                    var appCastVersion = item.SelectSingleNode("version");
                    if (appCastVersion != null)
                    {
                        var appVersion = appCastVersion.InnerText;
                        CurrentVersion = new Version(appVersion);
                    }
                    else
                        continue;

                    var appCastTitle = item.SelectSingleNode("title");

                    DialogTitle = appCastTitle?.InnerText ?? "";

                    var appCastChangeLog = item.SelectSingleNode("changelog");

                    ChangeLogUrl = GetUrl(webResponse.ResponseUri, appCastChangeLog);

                    var appCastUrl = item.SelectSingleNode("url");

                    DownloadUrl = GetUrl(webResponse.ResponseUri, appCastUrl);

                    if (IntPtr.Size.Equals(8))
                    {
                        var appCastUrl64 = item.SelectSingleNode("url64");

                        var downloadUrl64 = GetUrl(webResponse.ResponseUri, appCastUrl64);

                        if (!string.IsNullOrEmpty(downloadUrl64))
                            DownloadUrl = downloadUrl64;
                    }
                }

            if (updateKey != null)
            {
                var skip = updateKey.GetValue("skip");
                var applicationVersion = updateKey.GetValue("version");
                if ((skip != null) && (applicationVersion != null))
                {
                    var skipValue = skip.ToString();
                    var skipVersion = new Version(applicationVersion.ToString());
                    if (skipValue.Equals("1") && (CurrentVersion <= skipVersion))
                        return;
                    if (CurrentVersion > skipVersion)
                    {
                        var updateKeyWrite = Registry.CurrentUser.CreateSubKey(RegistryLocation);
                        if (updateKeyWrite != null)
                        {
                            updateKeyWrite.SetValue("version", CurrentVersion.ToString());
                            updateKeyWrite.SetValue("skip", 0);
                        }
                    }
                }
                updateKey.Close();
            }

            if (CurrentVersion == null)
                return;

            var args = new UpdateInfoEventArgs
            {
                DownloadUrl = DownloadUrl,
                ChangelogUrl = ChangeLogUrl,
                CurrentVersion = CurrentVersion,
                InstalledVersion = InstalledVersion,
                IsUpdateAvailable = false
            };

            if (CurrentVersion > InstalledVersion)
            {
                args.IsUpdateAvailable = true;
                if (CheckForUpdateEvent == null)
                {
                    var thread = new Thread(ShowUi);
                    thread.CurrentCulture = thread.CurrentUICulture = CurrentCulture ?? Application.CurrentCulture;
                    thread.SetApartmentState(ApartmentState.STA);
                    thread.Start();
                }
            }

            CheckForUpdateEvent?.Invoke(args);
        }

        private static string GetUrl(Uri baseUri, XmlNode xmlNode)
        {
            var temp = xmlNode?.InnerText ?? "";

            if (!string.IsNullOrEmpty(temp) && Uri.IsWellFormedUriString(temp, UriKind.Relative))
            {
                var uri = new Uri(baseUri, temp);

                if (uri.IsAbsoluteUri)
                    temp = uri.AbsoluteUri;
            }

            return temp;
        }

        private static void ShowUi()
        {
            var updateForm = new UpdateForm();

            updateForm.ShowDialog();
        }

        private static Attribute GetAttribute(Assembly assembly, Type attributeType)
        {
            var attributes = assembly.GetCustomAttributes(attributeType, false);
            if (attributes.Length == 0)
                return null;
            return (Attribute) attributes[0];
        }

        /// <summary>
        ///     Opens the Download window that download the update and execute the installer when download completes.
        /// </summary>
        public static void DownloadUpdate()
        {
            var downloadDialog = new DownloadUpdateDialog(DownloadUrl);

            try
            {
                downloadDialog.ShowDialog();
            }
            catch (TargetInvocationException)
            {
            }
        }
    }
}