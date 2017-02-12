﻿// This file is Part of CalDavSynchronizer (http://outlookcaldavsynchronizer.sourceforge.net/)
// Copyright (c) 2015 Gerhard Zehetbauer
// Copyright (c) 2015 Alexander Nimmervoll
// 
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as
// published by the Free Software Foundation, either version 3 of the
// License, or (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
// 
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading.Tasks;
using CalDavSynchronizer.Contracts;
using CalDavSynchronizer.DataAccess;
using CalDavSynchronizer.Implementation;
using CalDavSynchronizer.Implementation.Common;
using CalDavSynchronizer.Implementation.ComWrappers;
using CalDavSynchronizer.Implementation.Contacts;
using CalDavSynchronizer.Implementation.DistributionLists;
using CalDavSynchronizer.Implementation.Events;
using CalDavSynchronizer.Implementation.GoogleContacts;
using CalDavSynchronizer.Implementation.GoogleTasks;
using CalDavSynchronizer.Implementation.Tasks;
using CalDavSynchronizer.Implementation.TimeRangeFiltering;
using CalDavSynchronizer.Implementation.TimeZones;
using CalDavSynchronizer.Synchronization;
using CalDavSynchronizer.Utilities;
using DDay.iCal;
using DDay.iCal.Serialization.iCalendar;
using GenSync.EntityRelationManagement;
using GenSync.EntityRepositories;
using GenSync.Logging;
using GenSync.ProgressReport;
using GenSync.Synchronization;
using GenSync.Synchronization.StateFactories;
using Google.Apis.Tasks.v1.Data;
using Google.Contacts;
using log4net;
using Microsoft.Office.Interop.Outlook;
using Thought.vCards;
using ContactEntityMapper = CalDavSynchronizer.Implementation.Contacts.ContactEntityMapper;
using Task = Google.Apis.Tasks.v1.Data.Task;

namespace CalDavSynchronizer.Scheduling
{
  public class SynchronizerFactory : ISynchronizerFactory
  {
    private static readonly ILog s_logger = LogManager.GetLogger (System.Reflection.MethodInfo.GetCurrentMethod().DeclaringType);

    private readonly string _outlookEmailAddress;
    private readonly ITotalProgressFactory _totalProgressFactory;
    private readonly NameSpace _outlookSession;
    private readonly Func<Guid, string> _profileDataDirectoryFactory;
    private readonly IDaslFilterProvider _daslFilterProvider;
    private readonly IOutlookAccountPasswordProvider _outlookAccountPasswordProvider;
    private readonly GlobalTimeZoneCache _globalTimeZoneCache;
    private readonly IQueryOutlookFolderStrategy _queryFolderStrategy;

    public SynchronizerFactory (Func<Guid, string> profileDataDirectoryFactory, ITotalProgressFactory totalProgressFactory, NameSpace outlookSession, IDaslFilterProvider daslFilterProvider, IOutlookAccountPasswordProvider outlookAccountPasswordProvider, GlobalTimeZoneCache globalTimeZoneCache, IQueryOutlookFolderStrategy queryFolderStrategy)
    {
      if (outlookAccountPasswordProvider == null)
        throw new ArgumentNullException (nameof (outlookAccountPasswordProvider));
      if (queryFolderStrategy == null) throw new ArgumentNullException(nameof(queryFolderStrategy));

      _outlookEmailAddress = string.Empty;
      try
      {
        using (var currentUser = GenericComObjectWrapper.Create (outlookSession.CurrentUser))
        {
          if (currentUser.Inner != null)
          {
            using (var addressEntry = GenericComObjectWrapper.Create (currentUser.Inner.AddressEntry))
            {
              if (addressEntry.Inner != null)
              {
                _outlookEmailAddress = OutlookUtility.GetEmailAdressOrNull (addressEntry.Inner, NullEntitySynchronizationLogger.Instance, s_logger) ?? string.Empty;
              }
            }
          }
        }
      }
      catch (COMException ex)
      {
        s_logger.Error ("Can't access currentuser email adress.", ex);
      }

      _totalProgressFactory = totalProgressFactory;
      _outlookSession = outlookSession;
      _daslFilterProvider = daslFilterProvider;
      _outlookAccountPasswordProvider = outlookAccountPasswordProvider;
      _profileDataDirectoryFactory = profileDataDirectoryFactory;
      _globalTimeZoneCache = globalTimeZoneCache;
      _queryFolderStrategy = queryFolderStrategy;
    }

    /// <summary>
    /// Components of the created synchronizer 
    /// Fields may be null, if the synchronizer doesn't use the component specified by the field 
    /// </summary>
    public class AvailableSynchronizerComponents
    {
      public ICalDavDataAccess CalDavDataAccess;
      public ICardDavDataAccess CardDavDataAccess;
      public ICardDavDataAccess DistListDataAccess;
    }

    public async Task<IOutlookSynchronizer> CreateSynchronizer (Options options, GeneralOptions generalOptions)
    {
      if (options == null)
        throw new ArgumentNullException (nameof (options));
      if (generalOptions == null)
        throw new ArgumentNullException (nameof (generalOptions));

      return (await CreateSynchronizerWithComponents(options, generalOptions)).Item1;
    }

    public async Task<Tuple<IOutlookSynchronizer, AvailableSynchronizerComponents>> CreateSynchronizerWithComponents (Options options, GeneralOptions generalOptions)
    {
      if (options == null)
        throw new ArgumentNullException (nameof (options));
      if (generalOptions == null)
        throw new ArgumentNullException (nameof (generalOptions));

      var synchronizerComponents = new AvailableSynchronizerComponents();

      OlItemType defaultItemType;
      string folderName;

      using (var outlookFolderWrapper = GenericComObjectWrapper.Create ((Folder) _outlookSession.GetFolderFromID (options.OutlookFolderEntryId, options.OutlookFolderStoreId)))
      {
        defaultItemType = outlookFolderWrapper.Inner.DefaultItemType;
        folderName = outlookFolderWrapper.Inner.Name;
      }

      IOutlookSynchronizer synchronizer;

      switch (defaultItemType)
      {
        case OlItemType.olAppointmentItem:
          synchronizer = await CreateEventSynchronizer (options, generalOptions, synchronizerComponents);
          break;
        case OlItemType.olTaskItem:
          if (options.ServerAdapterType == ServerAdapterType.GoogleTaskApi)
            synchronizer = await CreateGoogleTaskSynchronizer (options);
          else
            synchronizer = CreateTaskSynchronizer (options, generalOptions, synchronizerComponents);
          break;
        case OlItemType.olContactItem:
          if (options.ServerAdapterType == ServerAdapterType.GoogleContactApi)
            synchronizer = await CreateGoogleContactSynchronizer (options, synchronizerComponents);
          else
            synchronizer = CreateContactSynchronizer (options, generalOptions, synchronizerComponents);
          break;
        default:
          throw new NotSupportedException (
              string.Format (
                  "The folder '{0}' contains an item type ('{1}'), whis is not supported for synchronization",
                  folderName,
                  defaultItemType));
      }

      return Tuple.Create(synchronizer, synchronizerComponents);
    }

    private async Task<IOutlookSynchronizer> CreateEventSynchronizer (Options options, GeneralOptions generalOptions ,AvailableSynchronizerComponents componentsToFill)
    {
      ICalDavDataAccess calDavDataAccess;

      var calendarUrl = new Uri (options.CalenderUrl);

      if (calendarUrl.Scheme == Uri.UriSchemeFile)
      {
        calDavDataAccess = new FileSystemDavDataAccess (calendarUrl);
      }
      else
      {
        calDavDataAccess = new CalDavDataAccess(
          calendarUrl,
          CreateWebDavClient(options, _outlookAccountPasswordProvider, generalOptions));
      }

      componentsToFill.CalDavDataAccess = calDavDataAccess;

      var storageDataDirectory = _profileDataDirectoryFactory (options.Id);

      var entityRelationDataAccess = new EntityRelationDataAccess<AppointmentId, DateTime, OutlookEventRelationData, WebResourceName, string> (storageDataDirectory);

      return await CreateEventSynchronizer (options, calDavDataAccess, entityRelationDataAccess);
    }

    public static IWebDavClient CreateWebDavClient (
      Options options, 
      IOutlookAccountPasswordProvider outlookAccountPasswordProvider,
      GeneralOptions generalOptions)
    {
      if (outlookAccountPasswordProvider == null)
        throw new ArgumentNullException (nameof (outlookAccountPasswordProvider));
      if (generalOptions == null)
        throw new ArgumentNullException (nameof (generalOptions));

      return CreateWebDavClient (
          options.UserName,
          options.GetEffectivePassword(outlookAccountPasswordProvider),
          options.CalenderUrl,
          generalOptions.CalDavConnectTimeout,
          options.ServerAdapterType,
          options.CloseAfterEachRequest,
          options.PreemptiveAuthentication,
          options.ForceBasicAuthentication,
          options.ProxyOptions,
          generalOptions.EnableClientCertificate,
          generalOptions.AcceptInvalidCharsInServerResponse);
    }

    public static IWebDavClient CreateWebDavClient (
        string username,
        SecureString password,
        string serverUrl,
        TimeSpan timeout,
        ServerAdapterType serverAdapterType,
        bool closeConnectionAfterEachRequest,
        bool preemptiveAuthentication,
        bool forceBasicAuthentication,
        ProxyOptions proxyOptions,
        bool enableClientCertificate,
        bool acceptInvalidChars
        )
    {
      switch (serverAdapterType)
      {
        case ServerAdapterType.WebDavHttpClientBased:
        case ServerAdapterType.WebDavHttpClientBasedWithGoogleOAuth:
          var productAndVersion = GetProductAndVersion();
          return new DataAccess.HttpClientBasedClient.WebDavClient (
              () => CreateHttpClient (username, password, serverUrl, timeout, serverAdapterType, proxyOptions, preemptiveAuthentication, forceBasicAuthentication, enableClientCertificate),
              productAndVersion.Item1,
              productAndVersion.Item2,
              closeConnectionAfterEachRequest,
              acceptInvalidChars,
              RequiresEtagsWithoutQuotes(serverUrl));

        default:
          throw new ArgumentOutOfRangeException ("serverAdapterType");
      }
    }

    private static bool RequiresEtagsWithoutQuotes (string serverUrl)
    {
      var authorityParts = new Uri (serverUrl).Authority.ToLower().Split('.');
      if (authorityParts.Length >= 2)
      {
        var length = authorityParts.Length;
        if (authorityParts[length - 1] == "ru" && authorityParts[length - 2] == "mail")
        {
          s_logger.Info ($"Detected url which requires etags without quotes ('{serverUrl}')");
          return true;
        }
      }

      return false;
    }

    private static async System.Threading.Tasks.Task<HttpClient> CreateHttpClient ( string username,
                                                                                    SecureString password,
                                                                                    string serverUrl, 
                                                                                    TimeSpan calDavConnectTimeout, 
                                                                                    ServerAdapterType serverAdapterType, 
                                                                                    ProxyOptions proxyOptions, 
                                                                                    bool preemptiveAuthentication,
                                                                                    bool forceBasicAuthentication,
                                                                                    bool enableClientCertificate )
    {
      IWebProxy proxy = (proxyOptions != null) ? CreateProxy (proxyOptions) : null;

      switch (serverAdapterType)
      {
        case ServerAdapterType.WebDavHttpClientBased:
          var httpClientHandler = new HttpClientHandler();
          if (!string.IsNullOrEmpty (username))
          {
            var credentials = new NetworkCredential (username, password);
            if (forceBasicAuthentication)
            {
              var cache = new CredentialCache();
              cache.Add (new Uri (new Uri (serverUrl).GetLeftPart (UriPartial.Authority)), "Basic", credentials);
              httpClientHandler.Credentials = cache;
            }
            else
            {
              httpClientHandler.Credentials = credentials;
            }

            httpClientHandler.AllowAutoRedirect = false;
            httpClientHandler.PreAuthenticate = preemptiveAuthentication;
          }
          httpClientHandler.Proxy = proxy;
          httpClientHandler.UseProxy = (proxy != null);
          if (enableClientCertificate)
            httpClientHandler.ClientCertificateOptions = ClientCertificateOption.Automatic;

          var httpClient = new HttpClient (httpClientHandler);
          httpClient.Timeout = calDavConnectTimeout;
          return httpClient;
        case ServerAdapterType.WebDavHttpClientBasedWithGoogleOAuth:
          return await OAuth.Google.GoogleHttpClientFactory.CreateHttpClient (username, GetProductWithVersion(), proxy);
        default:
          throw new ArgumentOutOfRangeException ("serverAdapterType");
      }
    }

    public static IWebProxy CreateProxy (ProxyOptions proxyOptions)
    {
      IWebProxy proxy = null;

      if (proxyOptions.ProxyUseDefault)
      {
        proxy = WebRequest.DefaultWebProxy;
        proxy.Credentials = CredentialCache.DefaultCredentials;
      }
      else if (proxyOptions.ProxyUseManual)
      {
        proxy = new WebProxy (proxyOptions.ProxyUrl, false);
        if (!string.IsNullOrEmpty (proxyOptions.ProxyUserName))
        {
          proxy.Credentials = new NetworkCredential (proxyOptions.ProxyUserName, proxyOptions.ProxyPassword);
        }
        else
        {
          proxy.Credentials = CredentialCache.DefaultCredentials;
        }
      }
      return proxy;
    }

    private static Tuple<string, string> GetProductAndVersion ()
    {
      var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
      return Tuple.Create ("CalDavSynchronizer", string.Format ("{0}.{1}", version.Major, version.Minor));
    }

    private static string GetProductWithVersion ()
    {
      var productAndVersion = GetProductAndVersion();
      return string.Format ("{0}/{1}", productAndVersion.Item1, productAndVersion.Item2);
    }

    /// <remarks>
    /// Public because it is being used by integration tests
    /// </remarks>
    public async Task<IOutlookSynchronizer> CreateEventSynchronizer (
        Options options,
        ICalDavDataAccess calDavDataAccess,
        IEntityRelationDataAccess<AppointmentId, DateTime, WebResourceName, string> entityRelationDataAccess)
    {
      var dateTimeRangeProvider =
          options.IgnoreSynchronizationTimeRange ?
              NullDateTimeRangeProvider.Instance :
              new DateTimeRangeProvider (options.DaysToSynchronizeInThePast, options.DaysToSynchronizeInTheFuture);

      var mappingParameters = GetMappingParameters<EventMappingConfiguration> (options);

      var atypeRepository = new OutlookEventRepository (
          _outlookSession,
          options.OutlookFolderEntryId,
          options.OutlookFolderStoreId,
          dateTimeRangeProvider,
          mappingParameters,
          _daslFilterProvider,
          _queryFolderStrategy);

      IEntityRepository<WebResourceName, string, IICalendar, IEventSynchronizationContext> btypeRepository = new CalDavRepository<IEventSynchronizationContext> (
          calDavDataAccess,
          new iCalendarSerializer(),
          CalDavRepository.EntityType.Event,
          dateTimeRangeProvider,
          options.ServerAdapterType == ServerAdapterType.WebDavHttpClientBasedWithGoogleOAuth,
          options.IsChunkedSynchronizationEnabled ? new ChunkedExecutor(options.ChunkSize) : NullChunkedExecutor.Instance);

      var timeZoneCache = new TimeZoneCache (CreateHttpClient(options.ProxyOptions), mappingParameters.IncludeHistoricalData, _globalTimeZoneCache);

      ITimeZone configuredEventTimeZoneOrNull;

      if (mappingParameters.UseIanaTz)
        configuredEventTimeZoneOrNull = await timeZoneCache.GetByTzIdOrNull(mappingParameters.EventTz);
      else
        configuredEventTimeZoneOrNull = null;

      var entityMapper = new EventEntityMapper (
          _outlookEmailAddress, new Uri ("mailto:" + options.EmailAddress),
          _outlookSession.Application.TimeZones.CurrentTimeZone.ID,
          _outlookSession.Application.Version,
          timeZoneCache,
          mappingParameters,
          configuredEventTimeZoneOrNull);

      var outlookEventRelationDataFactory = new OutlookEventRelationDataFactory();

      var syncStateFactory = new EntitySyncStateFactory<AppointmentId, DateTime, AppointmentItemWrapper, WebResourceName, string, IICalendar, IEventSynchronizationContext> (
          entityMapper,
          outlookEventRelationDataFactory,
          ExceptionHandler.Instance
          );

      var btypeIdEqualityComparer = WebResourceName.Comparer;
      var atypeIdEqualityComparer = AppointmentId.Comparer;

      var aTypeWriteRepository = BatchEntityRepositoryAdapter.Create (atypeRepository);
      var bTypeWriteRepository = BatchEntityRepositoryAdapter.Create (btypeRepository);

      var synchronizer = new Synchronizer<AppointmentId, DateTime, AppointmentItemWrapper, WebResourceName, string, IICalendar, IEventSynchronizationContext> (
          atypeRepository,
          btypeRepository,
          aTypeWriteRepository,
          bTypeWriteRepository,
          InitialSyncStateCreationStrategyFactory<AppointmentId, DateTime, AppointmentItemWrapper, WebResourceName, string, IICalendar, IEventSynchronizationContext>.Create (
              syncStateFactory,
              syncStateFactory.Environment,
              options.SynchronizationMode,
              options.ConflictResolution,
              e => new EventConflictInitialSyncStateCreationStrategyAutomatic (e)),
          entityRelationDataAccess,
          outlookEventRelationDataFactory,
          new InitialEventEntityMatcher (btypeIdEqualityComparer),
          atypeIdEqualityComparer,
          btypeIdEqualityComparer,
          _totalProgressFactory,
          EqualityComparer<DateTime>.Default,
          EqualityComparer<string>.Default,
          syncStateFactory,
          new EventSynchronizationInterceptorFactory());

      return new OutlookEventSynchronizer<WebResourceName, string> (
        new ContextCreatingSynchronizerDecorator<AppointmentId, DateTime, AppointmentItemWrapper, WebResourceName, string, IICalendar, IEventSynchronizationContext>(
          synchronizer,
          new EventSynchronizationContextFactory(atypeRepository, btypeRepository, entityRelationDataAccess, mappingParameters.CleanupDuplicateEvents, atypeIdEqualityComparer)));
    }

    private HttpClient CreateHttpClient(ProxyOptions proxyOptionsOrNull)
    {
      var proxy = proxyOptionsOrNull != null ? CreateProxy (proxyOptionsOrNull) : null;
      var httpClientHandler = new HttpClientHandler
      {
        Proxy = proxy,
        UseProxy = proxy != null
      };

      return new HttpClient (httpClientHandler);
    }

    private T GetMappingParameters<T> (Options options)
        where T : class, new()
    {
      if (options.MappingConfiguration == null)
        return new T();

      var parameters = options.MappingConfiguration as T;

      if (parameters != null)
        return parameters;

      s_logger.ErrorFormat (
          "Expected mapping parameters of type '{0}', but found type of '{1}'. Falling back to default",
          typeof (T).Name,
          options.MappingConfiguration.GetType().Name);
      return new T();
    }

    private IOutlookSynchronizer CreateTaskSynchronizer (Options options, GeneralOptions generalOptions, AvailableSynchronizerComponents componentsToFill)
    {
      var mappingParameters = GetMappingParameters<TaskMappingConfiguration> (options);

      var atypeRepository = new OutlookTaskRepository (_outlookSession, options.OutlookFolderEntryId, options.OutlookFolderStoreId, _daslFilterProvider, mappingParameters, _queryFolderStrategy);



      ICalDavDataAccess calDavDataAccess;
      var calendarUrl = new Uri (options.CalenderUrl);

      if (calendarUrl.Scheme == Uri.UriSchemeFile)
      {
        calDavDataAccess = new FileSystemDavDataAccess (calendarUrl);
      }
      else
      {
        calDavDataAccess = new CalDavDataAccess (
         calendarUrl,
         CreateWebDavClient (
           options.UserName,
           options.GetEffectivePassword (_outlookAccountPasswordProvider),
           options.CalenderUrl,
           generalOptions.CalDavConnectTimeout,
           options.ServerAdapterType,
           options.CloseAfterEachRequest,
           options.PreemptiveAuthentication,
           options.ForceBasicAuthentication,
           options.ProxyOptions,
           generalOptions.EnableClientCertificate,
           generalOptions.AcceptInvalidCharsInServerResponse));
      }

      componentsToFill.CalDavDataAccess = calDavDataAccess;

      var btypeRepository = new CalDavRepository<int> (
          calDavDataAccess,
          new iCalendarSerializer(),
          CalDavRepository.EntityType.Todo,
          NullDateTimeRangeProvider.Instance,
          false,
          options.IsChunkedSynchronizationEnabled ? new ChunkedExecutor (options.ChunkSize) : NullChunkedExecutor.Instance);

      var relationDataFactory = new TaskRelationDataFactory ();
      var syncStateFactory = new EntitySyncStateFactory<string, DateTime, TaskItemWrapper, WebResourceName, string, IICalendar, int> (
          new TaskMapper (_outlookSession.Application.TimeZones.CurrentTimeZone.ID, mappingParameters),
          relationDataFactory,
          ExceptionHandler.Instance);

      var storageDataDirectory = _profileDataDirectoryFactory (options.Id);

      var btypeIdEqualityComparer = WebResourceName.Comparer;
      var atypeIdEqualityComparer = EqualityComparer<string>.Default;

      var atypeWriteRepository = BatchEntityRepositoryAdapter.Create (atypeRepository);
      var btypeWriteRepository = BatchEntityRepositoryAdapter.Create (btypeRepository);

      var synchronizer = new Synchronizer<string, DateTime, TaskItemWrapper, WebResourceName, string, IICalendar, int> (
          atypeRepository,
          btypeRepository,
          atypeWriteRepository,
          btypeWriteRepository,
          InitialSyncStateCreationStrategyFactory<string, DateTime, TaskItemWrapper, WebResourceName, string, IICalendar, int>.Create (
              syncStateFactory,
              syncStateFactory.Environment,
              options.SynchronizationMode,
              options.ConflictResolution,
              e => new TaskConflictInitialSyncStateCreationStrategyAutomatic (e)),
          new EntityRelationDataAccess<string, DateTime, TaskRelationData, WebResourceName, string> (storageDataDirectory),
          relationDataFactory,
          new InitialTaskEntityMatcher (btypeIdEqualityComparer),
          atypeIdEqualityComparer,
          btypeIdEqualityComparer,
          _totalProgressFactory,
          EqualityComparer<DateTime>.Default,
          EqualityComparer<string>.Default,
          syncStateFactory);

      return new OutlookSynchronizer<WebResourceName, string> (
        new NullContextSynchronizerDecorator<string, DateTime, TaskItemWrapper, WebResourceName, string, IICalendar> (synchronizer));
    }

    private async Task<IOutlookSynchronizer> CreateGoogleTaskSynchronizer (Options options)
    {
      var mappingParameters = GetMappingParameters<TaskMappingConfiguration> (options);

      var atypeRepository = new OutlookTaskRepository (_outlookSession, options.OutlookFolderEntryId, options.OutlookFolderStoreId, _daslFilterProvider, mappingParameters, _queryFolderStrategy);

      IWebProxy proxy = options.ProxyOptions != null ? CreateProxy (options.ProxyOptions) : null;

      var tasksService = await OAuth.Google.GoogleHttpClientFactory.LoginToGoogleTasksService (options.UserName,proxy);

      TaskList taskList;
      try
      {
        taskList = tasksService.Tasklists.Get (options.CalenderUrl).Execute();
      }
      catch (Google.GoogleApiException)
      {
        s_logger.ErrorFormat ($"Profile '{options.Name}' (Id: '{options.Id}'): task list '{options.CalenderUrl}' not found.");
        throw;
      }

      var btypeRepository = new GoogleTaskRepository (tasksService, taskList);

      
      var relationDataFactory = new GoogleTaskRelationDataFactory ();
      var syncStateFactory = new EntitySyncStateFactory<string, DateTime, TaskItemWrapper, string, string, Task, int> (
          new GoogleTaskMapper (),
          relationDataFactory,
          ExceptionHandler.Instance);

      var storageDataDirectory = _profileDataDirectoryFactory (options.Id);

      var btypeIdEqualityComparer = EqualityComparer<string>.Default;
      var atypeIdEqualityComparer = EqualityComparer<string>.Default;

      var atypeWriteRepository = BatchEntityRepositoryAdapter.Create (atypeRepository);
      var btypeWriteRepository = BatchEntityRepositoryAdapter.Create (btypeRepository);

      var synchronizer = new Synchronizer<string, DateTime, TaskItemWrapper, string, string, Task, int> (
          atypeRepository,
          btypeRepository,
          atypeWriteRepository,
          btypeWriteRepository,
          InitialSyncStateCreationStrategyFactory<string, DateTime, TaskItemWrapper, string, string, Task, int>.Create (
              syncStateFactory,
              syncStateFactory.Environment,
              options.SynchronizationMode,
              options.ConflictResolution,
              e => new GoogleTaskConflictInitialSyncStateCreationStrategyAutomatic (e)),
          new EntityRelationDataAccess<string, DateTime, GoogleTaskRelationData, string, string> (storageDataDirectory),
          relationDataFactory,
          new InitialGoogleTastEntityMatcher (btypeIdEqualityComparer),
          atypeIdEqualityComparer,
          btypeIdEqualityComparer,
          _totalProgressFactory,
          EqualityComparer<DateTime>.Default,
          EqualityComparer<string>.Default,
          syncStateFactory);

      return new OutlookSynchronizer<string, string> (
        new NullContextSynchronizerDecorator<string, DateTime, TaskItemWrapper, string, string, Task>( synchronizer));
    }

    private IOutlookSynchronizer CreateContactSynchronizer (Options options, GeneralOptions generalOptions, AvailableSynchronizerComponents componentsToFill)
    {
      var atypeRepository = new OutlookContactRepository<ICardDavRepositoryLogger> (
          _outlookSession,
          options.OutlookFolderEntryId,
          options.OutlookFolderStoreId,
          _daslFilterProvider,
          _queryFolderStrategy);

      ICardDavDataAccess cardDavDataAccess;
      var serverUrl = new Uri (options.CalenderUrl);

      IWebDavClient webDavClientOrNullIfFileAccess = null;

      if (serverUrl.Scheme == Uri.UriSchemeFile)
      {
        cardDavDataAccess = new FileSystemDavDataAccess(serverUrl);
      }
      else
      {
        webDavClientOrNullIfFileAccess = CreateWebDavClient(
          options.UserName,
          options.GetEffectivePassword(_outlookAccountPasswordProvider),
          options.CalenderUrl,
          generalOptions.CalDavConnectTimeout,
          options.ServerAdapterType,
          options.CloseAfterEachRequest,
          options.PreemptiveAuthentication,
          options.ForceBasicAuthentication,
          options.ProxyOptions,
          generalOptions.EnableClientCertificate,
          generalOptions.AcceptInvalidCharsInServerResponse);

        cardDavDataAccess = new CardDavDataAccess(
          serverUrl,
          webDavClientOrNullIfFileAccess,
            contentType  => contentType != "text/x-vlist");
      }
      componentsToFill.CardDavDataAccess = cardDavDataAccess;

      var chunkedExecutor = options.IsChunkedSynchronizationEnabled ? new ChunkedExecutor (options.ChunkSize) : NullChunkedExecutor.Instance;

      var btypeRepository =
        new LoggingCardDavRepositoryDecorator(
          new CardDavRepository(
            cardDavDataAccess,
            chunkedExecutor));

      var mappingParameters = GetMappingParameters<ContactMappingConfiguration> (options);

      var entityMapper = new ContactEntityMapper (mappingParameters);

      var entityRelationDataFactory = new OutlookContactRelationDataFactory();

      var syncStateFactory = new EntitySyncStateFactory<string, DateTime, ContactItemWrapper, WebResourceName, string, vCard, ICardDavRepositoryLogger> (
          entityMapper,
          entityRelationDataFactory,
          ExceptionHandler.Instance);

      var btypeIdEqualityComparer = WebResourceName.Comparer;
      var atypeIdEqulityComparer = EqualityComparer<string>.Default;

      var storageDataDirectory = _profileDataDirectoryFactory (options.Id);

      var storageDataAccess = new EntityRelationDataAccess<string, DateTime, OutlookContactRelationData, WebResourceName, string> (storageDataDirectory);

      var atypeWriteRepository = BatchEntityRepositoryAdapter.Create (atypeRepository);
      var btypeWriteRepository = BatchEntityRepositoryAdapter.Create (btypeRepository);

      var synchronizer = new Synchronizer<string, DateTime, ContactItemWrapper, WebResourceName, string, vCard, ICardDavRepositoryLogger> (
          atypeRepository,
          btypeRepository,
          atypeWriteRepository,
          btypeWriteRepository,
          InitialSyncStateCreationStrategyFactory<string, DateTime, ContactItemWrapper, WebResourceName, string, vCard, ICardDavRepositoryLogger>.Create (
              syncStateFactory,
              syncStateFactory.Environment,
              options.SynchronizationMode,
              options.ConflictResolution,
              e => new ContactConflictInitialSyncStateCreationStrategyAutomatic (e)),
          storageDataAccess,
          entityRelationDataFactory,
          new InitialContactEntityMatcher (btypeIdEqualityComparer),
          atypeIdEqulityComparer,
          btypeIdEqualityComparer,
          _totalProgressFactory,
          EqualityComparer<DateTime>.Default,
          EqualityComparer<string>.Default,
          syncStateFactory);

      if (mappingParameters.MapDistributionLists)
      {
        CardDavEntityRepository<DistributionList, int, DistributionListSychronizationContext> bDistListRepository;

        // TODO: muss auch für das file funktionieren
        ICardDavDataAccess distListDataAccess = new CardDavDataAccess(
          serverUrl,
          webDavClientOrNullIfFileAccess,
          contentType => contentType == "text/x-vlist");

        componentsToFill.DistListDataAccess = distListDataAccess;

        switch (mappingParameters.DistributionListType)
        {
          case DistributionListType.Sogo:
            bDistListRepository = new SogoDistributionListRepository(distListDataAccess, chunkedExecutor);
            break;
          default:
            throw new NotImplementedException($"{nameof(DistributionListType)} '{mappingParameters.DistributionListType}' not implemented.");
        }

        var distributionListSynchronizer = CreateDistListSynchronizer(options, generalOptions, bDistListRepository);

        return new OutlookSynchronizer<WebResourceName, string>(
          new ContactAndDistListSynchronizer(
            synchronizer,
            distributionListSynchronizer,
            new EmailAddressCacheDataAccess(Path.Combine(storageDataDirectory,"emailAddressCache.xml")),
            btypeRepository,
            storageDataAccess,
            atypeRepository,
            _outlookSession));
      }
      else
      {
        return new OutlookSynchronizer<WebResourceName, string>(
          new ContextCreatingSynchronizerDecorator<string, DateTime, ContactItemWrapper, WebResourceName, string, vCard, ICardDavRepositoryLogger>(
            synchronizer,
            new SynchronizationContextFactory<ICardDavRepositoryLogger>(() => NullCardDavRepositoryLogger.Instance)));
      }
    }

    private ISynchronizer<DistributionListSychronizationContext> CreateDistListSynchronizer (
      Options options, 
      GeneralOptions generalOptions,
      CardDavEntityRepository<DistributionList, int, DistributionListSychronizationContext> btypeRepository)
    {
      var atypeRepository = new OutlookDistListRepository<DistributionListSychronizationContext> (
          _outlookSession,
          options.OutlookFolderEntryId,
          options.OutlookFolderStoreId,
          _daslFilterProvider,
          _queryFolderStrategy);

      var entityMapper = new DistListEntityMapper ();

      var entityRelationDataFactory = new DistListRelationDataFactory ();

      var syncStateFactory = new EntitySyncStateFactory<string, DateTime, GenericComObjectWrapper<DistListItem>, WebResourceName, string, DistributionList, DistributionListSychronizationContext> (
          entityMapper,
          entityRelationDataFactory,
          ExceptionHandler.Instance);

      var btypeIdEqualityComparer = WebResourceName.Comparer;
      var atypeIdEqulityComparer = EqualityComparer<string>.Default;

      var storageDataDirectory = _profileDataDirectoryFactory (options.Id);

      var storageDataAccess = new EntityRelationDataAccess<string, DateTime, DistListRelationData, WebResourceName, string> (storageDataDirectory, "distList");

      var atypeWriteRepository = BatchEntityRepositoryAdapter.Create (atypeRepository);
      var btypeWriteRepository = BatchEntityRepositoryAdapter.Create (btypeRepository);

      var synchronizer = new Synchronizer<string, DateTime, GenericComObjectWrapper<DistListItem>, WebResourceName, string, DistributionList, DistributionListSychronizationContext> (
          atypeRepository,
          btypeRepository,
          atypeWriteRepository,
          btypeWriteRepository,
          InitialSyncStateCreationStrategyFactory<string, DateTime, GenericComObjectWrapper<DistListItem>, WebResourceName, string, DistributionList, DistributionListSychronizationContext>.Create (
              syncStateFactory,
              syncStateFactory.Environment,
              options.SynchronizationMode,
              options.ConflictResolution,
              e => new DistListConflictInitialSyncStateCreationStrategyAutomatic(e)),
          storageDataAccess,
          entityRelationDataFactory,
          new InitialDistListEntityMatcher (btypeIdEqualityComparer),
          atypeIdEqulityComparer,
          btypeIdEqualityComparer,
          _totalProgressFactory,
          EqualityComparer<DateTime>.Default,
          EqualityComparer<string>.Default,
          syncStateFactory);

      return synchronizer;
    }

    private async Task<IOutlookSynchronizer> CreateGoogleContactSynchronizer (Options options, AvailableSynchronizerComponents componentsToFill)
    {
      var atypeRepository = new OutlookContactRepository<GoogleContactContext> (
          _outlookSession,
          options.OutlookFolderEntryId,
          options.OutlookFolderStoreId,
          _daslFilterProvider,
          _queryFolderStrategy);

      IWebProxy proxy = options.ProxyOptions != null ? CreateProxy (options.ProxyOptions) : null;

      var googleApiExecutor = new GoogleApiOperationExecutor (await OAuth.Google.GoogleHttpClientFactory.LoginToContactsService (options.UserName, proxy));

      var mappingParameters = GetMappingParameters<ContactMappingConfiguration> (options);

      var atypeIdEqulityComparer = EqualityComparer<string>.Default;
      var btypeIdEqualityComparer = EqualityComparer<string>.Default;

      const int chunkSize = 100;

      var btypeRepository = new GoogleContactRepository (
        googleApiExecutor, 
        options.UserName,
        mappingParameters,
        btypeIdEqualityComparer,
        new ChunkedExecutor (chunkSize));

      var entityMapper = new GoogleContactEntityMapper (mappingParameters);

      var entityRelationDataFactory = new GoogleContactRelationDataFactory ();

      var syncStateFactory = new EntitySyncStateFactory<string, DateTime, ContactItemWrapper, string, GoogleContactVersion, GoogleContactWrapper, GoogleContactContext> (
          entityMapper,
          entityRelationDataFactory,
          ExceptionHandler.Instance);

      var storageDataDirectory = _profileDataDirectoryFactory (options.Id);

      var storageDataAccess = new EntityRelationDataAccess<string, DateTime, GoogleContactRelationData, string, GoogleContactVersion> (storageDataDirectory);

      var atypeWriteRepository = BatchEntityRepositoryAdapter.Create (atypeRepository);

      var synchronizer = new Synchronizer<string, DateTime, ContactItemWrapper, string, GoogleContactVersion, GoogleContactWrapper, GoogleContactContext> (
          atypeRepository,
          btypeRepository,
          atypeWriteRepository,
          btypeRepository,
          InitialSyncStateCreationStrategyFactory<string, DateTime, ContactItemWrapper, string, GoogleContactVersion, GoogleContactWrapper, GoogleContactContext>.Create (
              syncStateFactory,
              syncStateFactory.Environment,
              options.SynchronizationMode,
              options.ConflictResolution,
              e => new GoogleContactConflictInitialSyncStateCreationStrategyAutomatic (e)),
          storageDataAccess,
          entityRelationDataFactory,
          new InitialGoogleContactEntityMatcher (btypeIdEqualityComparer),
          atypeIdEqulityComparer,
          btypeIdEqualityComparer,
          _totalProgressFactory,
          EqualityComparer<DateTime>.Default,
          new GoogleContactVersionComparer(),
          syncStateFactory);

      return new OutlookSynchronizer<string, GoogleContactVersion> (
        new ContextCreatingSynchronizerDecorator<string, DateTime, ContactItemWrapper, string, GoogleContactVersion, GoogleContactWrapper, GoogleContactContext>(
          synchronizer,
          new GoogleContactContextFactory(googleApiExecutor, btypeIdEqualityComparer, options.UserName)));
    }
  }
}