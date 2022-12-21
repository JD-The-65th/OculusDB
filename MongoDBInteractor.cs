﻿using ComputerUtils.Logging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;
using OculusDB.Analytics;
using OculusDB.Database;
using OculusDB.QAVS;
using OculusDB.Users;
using OculusGraphQLApiLib;
using OculusGraphQLApiLib.Results;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace OculusDB
{
    public class MongoDBInteractor
    {
        public static MongoClient mongoClient = null;
        public static IMongoDatabase oculusDBDatabase = null;
        public static IMongoCollection<BsonDocument> dataCollection = null;
        public static IMongoCollection<BsonDocument> activityCollection = null;
        public static IMongoCollection<ActivityWebhook> webhookCollection = null;
        public static IMongoCollection<Analytic> analyticsCollection = null;
        public static IMongoCollection<AppToScrape> appsToScrape = null;
        public static IMongoCollection<AppToScrape> appsScraping = null;
        public static IMongoCollection<AppToScrape> scrapedApps = null;

		public static IMongoCollection<QAVSReport> qAVSReports = null;

		public static void AddAppToScrapeIfNotPresent(AppToScrape appToScrape)
        {
            if(appToScrape.priority)
            {
                if (appsToScrape.Count(x => x.appId == appToScrape.appId && !x.priority) > 0)
                {
                    appsToScrape.DeleteMany(x => x.appId == appToScrape.appId && !x.priority);
                }

                if (appsToScrape.Count(x => x.appId == appToScrape.appId && x.priority) <= 0 && appsScraping.Count(x => x.appId == appToScrape.appId) <= 0)
                {
                    appsToScrape.InsertOne(appToScrape);
                }
            } else
            {
                if (!IsAppScrapingOrQueuedToScrape(appToScrape))
                {
                    appsToScrape.InsertOne(appToScrape);
                }
            }
        }

        public static void RemoveScrapingAndToScrapeNonPriorityApps()
        {
            appsToScrape.DeleteMany(x => !x.priority);
            appsScraping.DeleteMany(x => true);
            scrapedApps.DeleteMany(x => true);
        }

        public static AppToScrape GetNextScrapeApp(bool priority)
        {
            return appsToScrape.Find(x => x.priority == priority).SortBy(x => x.addedTime).FirstOrDefault();
        }

        public static void MarkAppAsScraping(AppToScrape app)
        {
            //app._id = ObjectId.GenerateNewId();
            appsToScrape.DeleteMany(x => x.appId == app.appId);
            appsScraping.DeleteMany(x => x.appId == app.appId);
            appsScraping.InsertOne(app);
        }

        public static bool AreAppsToScrapePresent(bool priority)
        {
            return GetAppsToScrapeCount(priority) > 0;
        }

        public static long GetAppsToScrapeCount(bool priority)
        {
            return appsToScrape.Count(x => x.priority == priority);
        }
        public static long GetScrapedAppsCount(bool priority)
        {
            return scrapedApps.Count(x => x.priority == priority);
        }

        public static void MarkAppAsScrapedOrFailed(AppToScrape app)
        {
            Logger.Log("Marking " + app.appId + " as scraped");
            appsScraping.DeleteMany(x => x.appId == app.appId);
            if(!app.priority) scrapedApps.InsertOne(app);
        }

        public static bool IsAppScrapingOrQueuedToScrape(AppToScrape app)
        {
            return appsToScrape.Count(x => x.appId == app.appId) + appsScraping.Count(x => x.appId == app.appId) > 0;
        }

        public static void Initialize()
        {
            mongoClient = new MongoClient(OculusDBEnvironment.config.mongoDBUrl);
            oculusDBDatabase = mongoClient.GetDatabase(OculusDBEnvironment.config.mongoDBName);
            dataCollection = oculusDBDatabase.GetCollection<BsonDocument>("data");
            webhookCollection = oculusDBDatabase.GetCollection<ActivityWebhook>("webhooks");
            activityCollection = oculusDBDatabase.GetCollection<BsonDocument>("activity");
            analyticsCollection = oculusDBDatabase.GetCollection<Analytic>("analytics");

            appsScraping = oculusDBDatabase.GetCollection<AppToScrape>("appsScraping");
            appsToScrape = oculusDBDatabase.GetCollection<AppToScrape>("appsToScrape");
            scrapedApps = oculusDBDatabase.GetCollection<AppToScrape>("scrapedApps");
            qAVSReports = oculusDBDatabase.GetCollection<QAVSReport>("QAVSReports");

			ConventionPack pack = new ConventionPack();
            pack.Add(new IgnoreExtraElementsConvention(true));
            ConventionRegistry.Register("Ignore extra elements cause it's annoying", pack, t => true);

            // Don't ask. It's important stuff to reduce DB size and fix a lot of errors
            RemoveIdRemap<Application>();
            RemoveIdRemap<ParentApplication>();
            RemoveIdRemap<AndroidBinary>();
            RemoveIdRemap<AppStoreOffer>();
            RemoveIdRemap<DBVersion>();
            RemoveIdRemap<DBActivityNewApplication>();
            RemoveIdRemap<DBActivityNewVersion>();
            RemoveIdRemap<DBActivityVersionUpdated>();
            RemoveIdRemap<DBActivityPriceChanged>();
            RemoveIdRemap<DBActivityNewDLC>();
            RemoveIdRemap<DBActivityNewDLCPack>();
            RemoveIdRemap<DBActivityNewDLCPackDLC>();
            RemoveIdRemap<DBActivityDLCUpdated>();
            RemoveIdRemap<DBActivityDLCPackUpdated>();
			RemoveIdRemap<DBActivityVersionChangelogAvailable>();
			RemoveIdRemap<DBActivityVersionChangelogUpdated>();
			RemoveIdRemap<DBReleaseChannel>();
            RemoveIdRemap<DBApplication>();
            RemoveIdRemap<DBIAPItem>();

            BsonClassMap.RegisterClassMap<ReleaseChannel>(cm =>
            {
                cm.AutoMap();
                cm.UnmapProperty(x => x.latest_supported_binary); // Remove AndroidBinary
            });
            //RemoveIdRemap<IAPItem>();
            BsonClassMap.RegisterClassMap<IAPItem>(cm =>
            {
                cm.AutoMap();
                cm.UnmapProperty(x => x.parent_application);
                cm.UnmapProperty(x => x.latest_supported_asset_file);
                cm.UnmapProperty(x => x.id);
                cm.MapMember(x => x.id)
                    .SetElementName("id")
                    .SetOrder(0) //specific to your needs
                    .SetIsRequired(true); // again specific to your needs
            });
        }

        public static string AddQAVSReport(QAVSReport report)
        {
            string id = Random.Shared.Next(0, 0xFFFFFF).ToString("X");
            report.reportId = id;
            qAVSReports.DeleteMany(x => x.reportId == id);
            qAVSReports.InsertOne(report);
            return id;
		}

        public static QAVSReport GetQAVSReport(string id)
        {
			return qAVSReports.Find(x => x.reportId == id).FirstOrDefault();
		}

        public static void RemoveIdRemap<T>()
        {
            BsonClassMap.RegisterClassMap<T>(cm =>
            {
                cm.AutoMap();
                if (typeof(T).GetMember("id").Length > 0)
                {
                    Logger.Log("Unmapping reassignment for " + typeof(T).Name + " id -> _id");
                    cm.UnmapProperty("id");
                    cm.MapMember(typeof(T).GetMember("id")[0])
                        .SetElementName("id")
                        .SetOrder(0) //specific to your needs
                        .SetIsRequired(true); // again specific to your needs
                }
                
                if(typeof(T).GetMember("__id").Length > 0)
                {
                    Logger.Log("Unmapping reassignment for " + typeof(T).Name + " __id -> _id");
                    cm.UnmapProperty("__id");
                    cm.MapMember(typeof(T).GetMember("__id")[0])
                    .SetElementName("__id")
                    .SetOrder(0) //specific to your needs
                    .SetIsRequired(true); // again specific to your needs
                }
            });
        }

        public static void AddAnalytic(Analytic a)
        {
            analyticsCollection.InsertOne(a);
        }

        public static List<Analytic> GetAllAnalyticsForApplication(string parentApplicationId, DateTime after)
        {
            return analyticsCollection.Aggregate<Analytic>(new BsonDocument[]
{
    new BsonDocument("$match",
    new BsonDocument
        {
            { "parentId", parentApplicationId },
            { "reported",
    new BsonDocument("$gte",
    after) }
        }),
    new BsonDocument("$group",
    new BsonDocument
        {
            { "_id",
    new BsonDocument("id", "$itemId") },
            { "itemId",
    new BsonDocument("$first", "$itemId") },
            { "parentId",
    new BsonDocument("$first", "$parentId") },
            { "count",
    new BsonDocument("$sum", 1) }
        })
}).ToList();
        }

        public static List<Analytic> GetApplicationAnalytics(DateTime after, int skip = 0, int take = 50)
        {
            return analyticsCollection.Aggregate<Analytic>(new BsonDocument[]
{
    new BsonDocument("$match",
    new BsonDocument
        {
            { "reported",
    new BsonDocument("$gte",
    after) }
        }),
    new BsonDocument("$group",
    new BsonDocument
        {
            { "_id",
    new BsonDocument("id", "$parentId") },
            { "parentId",
    new BsonDocument("$first", "$parentId") },
            { "applicationName",
    new BsonDocument("$first", "$applicationName") },
            { "count",
    new BsonDocument("$sum", 1) }
        })
}).ToEnumerable().OrderByDescending(x => x.count).Skip(skip).Take(take).ToList();
        }

        public static long CountDataDocuments()
        {
            return dataCollection.CountDocuments(new BsonDocument());
        }

        public static long CountActivityDocuments()
        {
            return activityCollection.CountDocuments(new BsonDocument());
        }

        public static List<BsonDocument> GetApplicationByPackageName(string packageName)
        {
            return dataCollection.Find(x => x["__OculusDBType"] == DBDataTypes.Application && x["packageName"] == packageName).ToList();
        }

        public static List<BsonDocument> GetBestReviews(int skip, int take)
        {
            return GetDistinct(dataCollection.Find(x => x["__OculusDBType"] == DBDataTypes.Application).SortByDescending(x => x["quality_rating_aggregate"]).Skip(skip).Limit(take).ToList());
        }

        public static List<BsonDocument> GetName(int skip, int take)
        {
            return GetDistinct(dataCollection.Find(x => x["__OculusDBType"] == DBDataTypes.Application).SortByDescending(x => x["display_name"]).Skip(skip).Limit(take).ToList());
        }

        public static List<BsonDocument> GetPub(int skip, int take)
        {
            return GetDistinct(dataCollection.Find(x => x["__OculusDBType"] == DBDataTypes.Application).SortByDescending(x => x["publisher_name"]).Skip(skip).Limit(take).ToList());
        }

        public static List<BsonDocument> GetRelease(int skip, int take)
        {
            return GetDistinct(dataCollection.Find(x => x["__OculusDBType"] == DBDataTypes.Application).SortByDescending(x => x["release_date"]).Skip(skip).Limit(take).ToList());
        }

        public static List<BsonDocument> GetLatestActivities(int count, int skip = 0, string typeConstraint = "", string applicationId = "")
        {
            string[] stuff = typeConstraint.Split(',');
            BsonArray a = new BsonArray();
			foreach (string s in stuff) a.Add(new BsonDocument("__OculusDBType", s));
            BsonDocument q = new BsonDocument();
			BsonArray and = new BsonArray();
			if (typeConstraint != "") and.Add(new BsonDocument("$or", a));


			if (applicationId != "")
            {
				BsonArray orContitionsForApplication = new BsonArray();
				orContitionsForApplication.Add(new BsonDocument("id", applicationId));
				orContitionsForApplication.Add(new BsonDocument("parentApplication.id", applicationId));
				and.Add(new BsonDocument("$or", orContitionsForApplication));
			}
            q.Add(new BsonDocument("$and", and));

			return activityCollection.Find(q).SortByDescending(x => x["__lastUpdated"]).Skip(skip).Limit(count).ToList();
        }
        public static List<BsonDocument> GetActivityById(string id)
        {
            return activityCollection.Find(x => x["_id"] == new ObjectId(id)).ToList();
        }

        public static BsonDocument GetLastEventWithIDInDatabase(string id)
        {
            return activityCollection.Find(x => x["id"] == id).SortByDescending(x => x["__lastUpdated"]).FirstOrDefault();
        }

        public static List<BsonDocument> GetLatestActivities(DateTime after)
        {
            return activityCollection.Find(x => x["__lastUpdated"] >= after).SortByDescending(x => x["__lastUpdated"]).ToList();
        }

        public static long DeleteOldData(DateTime before, List<string> ids)
        {
            long deleted = 0;
            for(int i = 0; i < ids.Count; i++)
            {
                try
                {
                    deleted += dataCollection.DeleteMany(x => x["__lastUpdated"] < before && ((x["__OculusDBType"] == DBDataTypes.Application && x["id"] == ids[i]) || (x["__OculusDBType"] != DBDataTypes.Application && x["__OculusDBType"] != DBDataTypes.Version && x["parentApplication"]["id"] == ids[i]))).DeletedCount;
                }
                catch
                {
                    i--;
                    Logger.Log("Sleeping for 5000 ms before continuing to delete old data due to error");
                    Thread.Sleep(5000);
                }
            }
            return deleted;
        }

        public static long DeleteOldDataExceptVersions(DateTime before)
        {
            List<AppToScrape> toDelete = scrapedApps.Find(x => !x.priority).ToList();
            long deleted = 0;
            for (int i = 0; i < toDelete.Count; i++)
            {
                try
                {
                    deleted += dataCollection.DeleteMany(x => x["__lastUpdated"] < before && ((x["__OculusDBType"] == DBDataTypes.Application && x["id"] == toDelete[i].appId) || (x["__OculusDBType"] != DBDataTypes.Application && x["__OculusDBType"] != DBDataTypes.Version && x["parentApplication"]["id"] == toDelete[i].appId))).DeletedCount;
                }
                catch
                {
                    i--;
                    Logger.Log("Sleeping for 5000 ms before continuing to delete old data due to error");
                    Thread.Sleep(5000);
                }
            }
            return deleted;
        }

        public static long DeleteOldVersions(DateTime before, string appId, List<string> versions)
        {
            long deleted = 0;
            
            try
            {
                while(versions.Count > 0)
                {
                    Logger.Log(dataCollection.Count(x => x["__lastUpdated"] < before && x["id"] == versions[0]) + " versions of " + versions[0] + " to delete before " + before);
                    deleted += dataCollection.DeleteMany(x => x["__lastUpdated"] < before && x["id"] == versions[0]).DeletedCount;
                    versions.RemoveAt(0);
                }
            }
            catch
            {
                Logger.Log("Sleeping for 5000 ms before continuing to delete old data due to error");
                Thread.Sleep(5000);
            }
            return deleted;
        }

        public static List<ActivityWebhook> GetWebhooks()
        {
            return webhookCollection.Find(new BsonDocument()).ToList();
        }

        public static BsonDocument GetLastPriceChangeOfApp(string appId)
        {
            return activityCollection.Find(x => x["parentApplication"]["id"] == appId && x["__OculusDBType"] == DBDataTypes.ActivityPriceChanged).SortByDescending(x => x["__lastUpdated"]).FirstOrDefault();
        }

        public static List<BsonDocument> GetPriceChanges(string id)
        {
            return activityCollection.Find(x => (x["id"] == id || x["parentApplication"]["id"] == id && x["__OculusDBType"] == DBDataTypes.ActivityPriceChanged)).SortByDescending(x => x["__lastUpdated"]).ToList();
        }

        public static BsonDocument AddBsonDocumentToActivityCollection(BsonDocument d)
        {
            d["_id"] = ObjectId.GenerateNewId();
            activityCollection.InsertOne(d);
			return activityCollection.Find<BsonDocument>(x => x["_id"] == d["_id"]).FirstOrDefault();
		}

        public static void AddApplication(Application a, Headset h, string image, string packageName)
        {
            DBApplication dba = ObjectConverter.ConvertCopy<DBApplication, Application>(a);
            dba.hmd = h;
            dba.img = image;
            dba.packageName = packageName;
            OculusScraper.DownloadImage(dba);
            dataCollection.InsertOne(dba.ToBsonDocument());
        }

        public static void AddVersion(AndroidBinary a, Application app, Headset h, DBVersion oldEntry = null)
        {
            DBVersion dba = ObjectConverter.ConvertCopy<DBVersion, AndroidBinary>(a);
            dba.parentApplication.id = app.id;
            dba.parentApplication.hmd = h;
            dba.parentApplication.displayName = app.displayName;
            dba.parentApplication.canonicalName = app.canonicalName;
            dba.__lastUpdated = DateTime.Now;
            
            if(oldEntry == null)
            {
                if (a.obb_binary != null)
                {
                    if (dba.obbList == null) dba.obbList = new List<OBBBinary>();
                    dba.obbList.Add(ObjectConverter.ConvertCopy<OBBBinary, AssetFile>(a.obb_binary));
                }
                foreach (AssetFile f in a.asset_files.nodes)
                {
                    if (dba.obbList == null) dba.obbList = new List<OBBBinary>();
                    if (f.is_required) dba.obbList.Add(ObjectConverter.ConvertCopy<OBBBinary, AssetFile>(f));
                }
            } else
            {
                dba.obbList = oldEntry.obbList;
            }

            dataCollection.InsertOne(dba.ToBsonDocument());
        }

        public static void AddDLCPack(AppItemBundle a, Headset h, Application app)
        {
            DBIAPItemPack dba = ObjectConverter.ConvertCopy<DBIAPItemPack, AppItemBundle, IAPItem>(a);
            dba.parentApplication.hmd = h;
            dba.parentApplication.displayName = app.displayName;
            foreach(Node<IAPItem> i in a.bundle_items.edges)
            {
                DBItemId id = new DBItemId();
                id.id = i.node.id;
                dba.bundle_items.Add(id);
            }
            dataCollection.InsertOne(dba.ToBsonDocument());
        }

        public static void AddDLC(IAPItem a, Headset h)
        {
            DBIAPItem dba = ObjectConverter.ConvertCopy<DBIAPItem, IAPItem>(a);
            dba.parentApplication.hmd = h;
            dba.latestAssetFileId = a.latest_supported_asset_file != null ? a.latest_supported_asset_file.id : "";
            dataCollection.InsertOne(dba.ToBsonDocument());
        }

        public static List<BsonDocument> GetByID(string id, int history = 1)
        {
            return dataCollection.Find(new BsonDocument("id", id)).SortByDescending(x => x["__lastUpdated"]).Limit(history).ToList();
        }

        public static ConnectedList GetConnected(string id)
        {
            ConnectedList l = new ConnectedList();
            List<BsonDocument> docs = GetByID(id);
            string applicationId = id;
            if(docs.Count() > 0)
			{
				BsonDocument org = docs.First();
                applicationId = org["__OculusDBType"] != DBDataTypes.Application ? org["parentApplication"]["id"].AsString : id;
			}
            BsonDocument q = new BsonDocument
            {
                new BsonDocument("$or", new BsonArray
                {
                    new BsonDocument("id", id),
                    new BsonDocument("id", applicationId),
                    new BsonDocument("parentApplication.id", applicationId)
                })
            };
            foreach(BsonDocument d in GetDistinct(dataCollection.Find(q).SortByDescending(x => x["__lastUpdated"]).ToEnumerable()))
            {
                if(d["__OculusDBType"] == DBDataTypes.Version) l.versions.Add(ObjectConverter.ConvertToDBType(d));
                else if(d["__OculusDBType"] == DBDataTypes.Application) l.applications.Add(ObjectConverter.ConvertToDBType(d));
                else if (d["__OculusDBType"] == DBDataTypes.IAPItemPack) l.dlcPacks.Add(ObjectConverter.ConvertToDBType(d));
                else if (d["__OculusDBType"] == DBDataTypes.IAPItem) l.dlcs.Add(ObjectConverter.ConvertToDBType(d));
            }
            l.versions = l.versions.OrderByDescending(x => x.versionCode).ToList();
            return l;
        }

        public static bool DoesIdExistInCurrentScrape(string id)
        {
            return dataCollection.Find(x => x["id"] == id && x["__lastUpdated"] >= OculusDBEnvironment.config.ScrapingResumeData.currentScrapeStart).CountDocuments() > 0;
        }

        public static DLCLists GetDLCs(string parentAppId)
        {
            BsonDocument q = new BsonDocument
            {
                new BsonDocument("$or", new BsonArray
                {
                    new BsonDocument("__OculusDBType", DBDataTypes.IAPItem),
                    new BsonDocument("__OculusDBType", DBDataTypes.IAPItemPack)
                }),
                new BsonElement("parentApplication.id", parentAppId)
                
            };

            DLCLists dlcs = new DLCLists();
            foreach (BsonDocument doc in GetDistinct(dataCollection.Find(q).SortByDescending(x => x["__lastUpdated"]).ToEnumerable()))
            {
                if (doc["__OculusDBType"] == DBDataTypes.IAPItem) dlcs.dlcs.Add(ObjectConverter.ConvertToDBType(doc));
                else dlcs.dlcPacks.Add(ObjectConverter.ConvertToDBType(doc));
            }
            return dlcs;
        }

        public static List<BsonDocument> GetDistinct(IEnumerable<BsonDocument> data)
        {
            List<BsonDocument> distinct = new List<BsonDocument>();
            foreach (BsonDocument d in data)
            {
                if (distinct.FirstOrDefault(x => x["id"] == d["id"]) == null) distinct.Add(d);
            }
            return distinct;
        }

        public static List<BsonDocument> GetAllApplications()
        {
            return GetDistinct(dataCollection.Find(new BsonDocument("__OculusDBType", DBDataTypes.Application)).SortByDescending(x => x["__lastUpdated"]).ToEnumerable());
        }

        public static List<BsonDocument> SearchApplication(string query, List<Headset> headsets, bool quick)
        {
            if (query == "") return new List<BsonDocument>();
            if (headsets.Count <= 0) return new List<BsonDocument>();
            BsonDocument regex = new BsonDocument("$regex", new BsonRegularExpression("/.*" + query.Replace(" ", ".*") + ".*/i"));
            BsonArray a = new BsonArray();
            BsonDocument q;
            if (!quick)
            {
                foreach (Headset h in headsets) a.Add(new BsonDocument("$or", new BsonArray
                {
                    new BsonDocument("hmd", h),
                    new BsonDocument("parentApplication.hmd", h)
                }));
                q = new BsonDocument() { new BsonDocument("$and", new BsonArray {
                    new BsonDocument("$or", new BsonArray
                {
                    new BsonDocument("__OculusDBType", DBDataTypes.Application),
                    new BsonDocument("__OculusDBType", DBDataTypes.IAPItem),
                    new BsonDocument("__OculusDBType", DBDataTypes.IAPItemPack)
                }), new BsonDocument("$or", new BsonArray
                {
                    new BsonDocument("displayName", regex),
                    new BsonDocument("canonicalName", regex),
                    new BsonDocument("publisher_name", regex),
                    new BsonDocument("packageName", regex),
                    new BsonDocument("id", query),

                }),
                    new BsonDocument("$or", a)
                })};
                return GetDistinct(dataCollection.Find(q).SortByDescending(x => x["__lastUpdated"]).ToEnumerable());
            } else
            {
                Logger.Log("The quick brown fox jumped over the lazy search");
                foreach (Headset h in headsets) a.Add(new BsonDocument("hmd", h));
                q = new BsonDocument() { new BsonDocument("$and", new BsonArray {
                    new BsonDocument("__OculusDBType", DBDataTypes.Application),
                    new BsonDocument("displayName", regex),
                    new BsonDocument("$or", a)
                })};
                return GetDistinct(dataCollection.Find(q).SortByDescending(x => x["__lastUpdated"]).ToEnumerable());
            }
            
        }
    }
}
