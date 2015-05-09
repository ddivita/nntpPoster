﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Principal;
using System.ServiceModel.Syndication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using log4net;
using Util;

namespace nntpAutoposter
{
    class IndexerVerifier
    {
        private static readonly ILog log = LogManager.GetLogger(
            System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private Object monitor = new Object();
        private AutoPosterConfig configuration;
        private Task MyTask;
        private Boolean StopRequested;

        public IndexerVerifier(AutoPosterConfig configuration)
        {
            this.configuration = configuration;
            StopRequested = false;
            MyTask = new Task(IndexerVerifierTask, TaskCreationOptions.LongRunning);
        }

        public void Start()
        {
            MyTask.Start();
        }

        public void Stop()
        {
            lock (monitor)
            {
                StopRequested = true;
                Monitor.Pulse(monitor);
            }            
            MyTask.Wait();
        }

        private void IndexerVerifierTask()
        {
            while (!StopRequested)
            {
                VerifyUploadsOnIndexer();
                lock (monitor)
                {
                    if (StopRequested)
                    {
                        break;
                    }
                    Monitor.Wait(monitor, configuration.VerifierIntervalMinutes * 60 * 1000);
                }
            }
        }

        private void VerifyUploadsOnIndexer()
        {
            foreach (var upload in DBHandler.Instance.GetUploadEntriesToVerify())
            {
                try
                {
                    String fullPath = Path.Combine(configuration.BackupFolder.FullName, upload.Name);
                    
                    Boolean backupExists = false;
                    backupExists = Directory.Exists(fullPath);
                    if(!backupExists)
                        backupExists = File.Exists(fullPath);

                    if (!backupExists)
                    {
                        Console.WriteLine("The upload [{0}] was removed from the backup folder, cancelling verification.", upload.Name);
                        upload.Cancelled = true;
                        DBHandler.Instance.UpdateUploadEntry(upload);
                        continue;
                    }

                    if ((DateTime.UtcNow - upload.UploadedAt.Value).TotalMinutes < configuration.MinRepostAgeMinutes)
                    {
                        Console.WriteLine("The upload [{0}] is younger than {1} minutes. Skipping check.", 
                            upload.CleanedName, configuration.MinRepostAgeMinutes);
                        continue;
                    }

                    if (UploadIsOnIndexer(upload))
                    {
                        upload.SeenOnIndexAt = DateTime.UtcNow;
                        DBHandler.Instance.UpdateUploadEntry(upload);
                        Console.WriteLine("Release [{0}] has been found on the indexer.", upload.CleanedName);

                        if (upload.RemoveAfterVerify)
                        {
                            FileAttributes attributes = File.GetAttributes(fullPath);
                            if (attributes.HasFlag(FileAttributes.Directory))
                                Directory.Delete(fullPath, true);
                            else
                                File.Delete(fullPath);
                        }
                    }
                    else
                    {
                        Console.WriteLine("Release [{0}] has NOT been found on the indexer. Checking if a repost is required.", 
                            upload.CleanedName);
                        RepostIfRequired(upload);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Could not verify release [{0}] on index:", upload.CleanedName);
                    Console.WriteLine(ex.ToString());
                    //TODO: Log.
                }
            }
        }

        private Boolean UploadIsOnIndexer(UploadEntry upload)
        {
            var postAge = (Int32)Math.Ceiling((DateTime.UtcNow - upload.UploadedAt.Value).TotalDays);
            String verificationGetUrl = String.Format(
                configuration.SearchUrl,
                Uri.EscapeDataString(upload.CleanedName),
                postAge);

            ServicePointManager.ServerCertificateValidationCallback = ServerCertificateValidationCallback;

            HttpWebRequest request = WebRequest.Create(verificationGetUrl) as HttpWebRequest;       //Mono does not support CreateHttp
            //request.ServerCertificateValidationCallback = ServerCertificateValidationCallback;    //Not implemented in mono
            request.Method = "GET";
            request.Timeout = 60*1000;
            HttpWebResponse response = request.GetResponse() as HttpWebResponse;
            if(response.StatusCode != HttpStatusCode.OK)
                throw new Exception("Error when verifying on indexer: "
                                + response.StatusCode + " " + response.StatusDescription);

            using (XmlReader xmlReader = XmlReader.Create(response.GetResponseStream()))
            {
                SyndicationFeed feed = SyndicationFeed.Load(xmlReader);
                foreach (var item in feed.Items)
                {
                    Decimal similarityPercentage =
                        LevenshteinDistance.SimilarityPercentage(item.Title.Text, upload.CleanedName);
                    if (similarityPercentage > configuration.VerifySimilarityPercentageTreshold)
                        return true;
                }
            }
            return false;
        }

        private bool ServerCertificateValidationCallback(object sender, System.Security.Cryptography.X509Certificates.X509Certificate certificate, System.Security.Cryptography.X509Certificates.X509Chain chain, System.Net.Security.SslPolicyErrors sslPolicyErrors)
        {
            return true;    //HACK: this should be worked out better, right now we accept all SSL Certs.
        }

        private void RepostIfRequired(UploadEntry upload)
        {
            var AgeInMinutes = (DateTime.UtcNow - upload.UploadedAt.Value).TotalMinutes;
            var repostTreshold = Math.Pow(upload.Size, (1 / 2.45)) / 60; 
            //This is a bit of guesswork, a 15 MB item will repost after about 15 minutes, 
            // a  5 GB item will repost after about 2h30.
            // a 15 GB item will repost after about 4h00.
            // a 50 GB item will repost after about 6h30.
            
            //In any case, it gets overruled by the configuration here.
            if (repostTreshold < configuration.MinRepostAgeMinutes)
                repostTreshold = configuration.MinRepostAgeMinutes;
            if (repostTreshold > configuration.MaxRepostAgeMinutes)
                repostTreshold = configuration.MaxRepostAgeMinutes;

            if(AgeInMinutes > repostTreshold)
            {
                Console.WriteLine("Could not find [{0}] after {1:F2} minutes, reposting.", upload.Name, repostTreshold);
                UploadEntry repost = new UploadEntry();
                repost.Name = upload.Name;
                repost.RemoveAfterVerify = upload.RemoveAfterVerify;
                repost.Cancelled = false;
                repost.Size = upload.Size;
                DBHandler.Instance.AddNewUploadEntry(repost);   
                //This implicitly cancels all other uploads with the same name so no need to update the upload itself.
            }
        }
    }
}
