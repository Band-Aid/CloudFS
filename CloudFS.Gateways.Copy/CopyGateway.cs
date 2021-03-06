﻿/*
The MIT License(MIT)

Copyright(c) 2015 IgorSoft

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Composition;
using System.IO;
using System.Linq;
using System.Security.Authentication;
using System.Threading.Tasks;
using CopyRestAPI;
using CopyRestAPI.Helpers;
using CopyRestAPI.Models;
using IgorSoft.CloudFS.Gateways.Copy.OAuth;
using IgorSoft.CloudFS.Interface;
using IgorSoft.CloudFS.Interface.Composition;
using IgorSoft.CloudFS.Interface.IO;

namespace IgorSoft.CloudFS.Gateways.Copy
{
    [ExportAsAsyncCloudGateway("Copy")]
    [ExportMetadata(nameof(CloudGatewayMetadata.CloudService), CopyGateway.SCHEMA)]
    [ExportMetadata(nameof(CloudGatewayMetadata.Capabilities), CopyGateway.CAPABILITIES)]
    [ExportMetadata(nameof(CloudGatewayMetadata.ServiceUri), CopyGateway.URL)]
    [ExportMetadata(nameof(CloudGatewayMetadata.ApiAssembly), nameof(CopyRestAPI))]
    [System.Diagnostics.DebuggerDisplay("{DebuggerDisplay(),nq}")]
    public sealed class CopyGateway : IAsyncCloudGateway
    {
        private const string SCHEMA = "copy";

        private const GatewayCapabilities CAPABILITIES = GatewayCapabilities.All ^ GatewayCapabilities.CopyItems ^ GatewayCapabilities.MoveItems ^ GatewayCapabilities.RenameItems;

        private const string URL = "https://www.copy.com";

        private const int RETRIES = 3;

        private class CopyContext
        {
            public CopyClient Client { get; }

            public CopyContext(CopyClient client)
            {
                Client = client;
            }
        }

        private readonly IDictionary<RootName, CopyContext> contextCache = new Dictionary<RootName, CopyContext>();

        private string settingsPassPhrase;

        [ImportingConstructor]
        public CopyGateway([Import(ExportContracts.SettingsPassPhrase)] string settingsPassPhrase)
        {
            this.settingsPassPhrase = settingsPassPhrase;
        }

        private async Task<CopyContext> RequireContextAsync(RootName root, string apiKey = null)
        {
            if (root == null)
                throw new ArgumentNullException(nameof(root));

            var result = default(CopyContext);
            if (!contextCache.TryGetValue(root, out result)) {
                var client = await OAuthAuthenticator.LoginAsync(root.UserName, apiKey, settingsPassPhrase);
                contextCache.Add(root, result = new CopyContext(client));
            }
            return result;
        }

        public async Task<bool> TryAuthenticateAsync(RootName root, string apiKey, IDictionary<string, string> parameters)
        {
            try {
                await RequireContextAsync(root, apiKey);
                return true;
            } catch (AuthenticationException) {
                return false;
            }
        }

        public async Task<DriveInfoContract> GetDriveAsync(RootName root, string apiKey, IDictionary<string, string> parameters)
        {
            var context = await RequireContextAsync(root, apiKey);

            var item = await AsyncFunc.RetryAsync<User, ServerException>(async () => await context.Client.UserManager.GetUserAsync(), RETRIES);

            return new DriveInfoContract(item.Id, item.Storage.Quota - item.Storage.Used, item.Storage.Used);
        }

        public async Task<RootDirectoryInfoContract> GetRootAsync(RootName root, string apiKey, IDictionary<string, string> parameters)
        {
            var context = await RequireContextAsync(root, apiKey);

            var item = await AsyncFunc.RetryAsync<FileSystem, ServerException>(async () => await context.Client.GetRootFolder(), RETRIES);
            var user = await AsyncFunc.RetryAsync<User, ServerException>(async () => await context.Client.UserManager.GetUserAsync(), RETRIES);

            return new RootDirectoryInfoContract(item.Id, DateTimeOffset.FromFileTime(Math.Max(0, user.CreatedTime)), DateTimeOffset.FromFileTime(Math.Max(0, item.ModifiedTime.Ticks)));
        }

        public async Task<IEnumerable<FileSystemInfoContract>> GetChildItemAsync(RootName root, DirectoryId parent)
        {
            var context = await RequireContextAsync(root);

            var items = await AsyncFunc.RetryAsync<FileSystem, ServerException>(async () => await context.Client.FileSystemManager.GetFileSystemInformationAsync(parent.Value), RETRIES);

            return items.Children.Select(i => i.ToFileSystemInfoContract());
        }

        public async Task<bool> ClearContentAsync(RootName root, FileId target, Func<FileSystemInfoLocator> locatorResolver)
        {
            if (locatorResolver == null)
                throw new ArgumentNullException(nameof(locatorResolver));

            var context = await RequireContextAsync(root);

            var locator = locatorResolver();
            var item = await AsyncFunc.RetryAsync<FileSystem, ServerException>(async () => await context.Client.FileSystemManager.UploadNewFileStreamAsync(locator.ParentId.Value, locator.Name, Stream.Null, true), RETRIES);

            return true;
        }

        public async Task<Stream> GetContentAsync(RootName root, FileId source)
        {
            var context = await RequireContextAsync(root);

            var stream = new MemoryStream();
            //await AsyncFunc.Retry<ServerException>(() => context.Client.FileSystemManager.DownloadFileStreamAsync(source.Value, stream), RETRIES);
            await context.Client.FileSystemManager.DownloadFileStreamAsync(source.Value, stream);
            stream.Seek(0, SeekOrigin.Begin);

            return stream;
        }

        public async Task<bool> SetContentAsync(RootName root, FileId target, Stream content, IProgress<ProgressValue> progress, Func<FileSystemInfoLocator> locatorResolver)
        {
            if (locatorResolver == null)
                throw new ArgumentNullException(nameof(locatorResolver));

            var context = await RequireContextAsync(root);

            var locator = locatorResolver();
            var stream = progress != null ? new ProgressStream(content, progress) : content;
            var item = await AsyncFunc.RetryAsync<FileSystem, ServerException>(async () => await context.Client.FileSystemManager.UploadNewFileStreamAsync(locator.ParentId.Value, locator.Name, stream, true), RETRIES);

            return true;
        }

        public Task<FileSystemInfoContract> CopyItemAsync(RootName root, FileSystemId source, string copyName, DirectoryId destination, bool recurse)
        {
            return Task.FromException<FileSystemInfoContract>(new NotSupportedException(Properties.Resources.CopyingOfFilesOrDirectoriesNotSupported));
        }

        public /*async*/ Task<FileSystemInfoContract> MoveItemAsync(RootName root, FileSystemId source, string moveName, DirectoryId destination, Func<FileSystemInfoLocator> locatorResolver)
        {
            //var context = await RequireContext(root);

            //if (string.IsNullOrEmpty(moveName))
            //    moveName = locatorResolver().Name;
            //var success = await AsyncFunc.Retry<bool, ServerException>(async () => await context.Client.FileSystemManager.MoveFileAsync(source.Value, destination.Value, moveName, false), RETRIES);
            //if (!success)
            //    throw new ApplicationException(string.Format(CultureInfo.CurrentCulture, Resources.MoveFailed, source.Value, destination.Value, moveName?.Insert(0, @"/") ?? string.Empty));

            //var movedItemPath = destination.Value.Substring(0, destination.Value.LastIndexOf('/')) + @"/" + moveName;
            //var item = await AsyncFunc.Retry<FileSystem, ServerException>(async () => await context.Client.FileSystemManager.GetFileSystemInformationAsync(movedItemPath), RETRIES);

            //return item.ToFileSystemInfoContract();
            return Task.FromException<FileSystemInfoContract>(new NotSupportedException(Properties.Resources.MovingOfFilesOrDirectoriesNotSupported));
        }

        public async Task<DirectoryInfoContract> NewDirectoryItemAsync(RootName root, DirectoryId parent, string name)
        {
            var context = await RequireContextAsync(root);

            var item = await AsyncFunc.RetryAsync<FileSystem, ServerException>(async () => await context.Client.FileSystemManager.CreateNewFolderAsync(parent.Value, name, false), RETRIES);

            return new DirectoryInfoContract(item.Id, item.Name, item.DateLastSynced, FileSystemExtensions.Later(item.DateLastSynced, item.ModifiedTime));
        }

        public async Task<FileInfoContract> NewFileItemAsync(RootName root, DirectoryId parent, string name, Stream content, IProgress<ProgressValue> progress)
        {
            if (content.Length == 0)
                return new ProxyFileInfoContract(name);

            var context = await RequireContextAsync(root);

            var stream = progress != null ? new ProgressStream(content, progress) : content;
            var item = await AsyncFunc.RetryAsync<FileSystem, ServerException>(async () => await context.Client.FileSystemManager.UploadNewFileStreamAsync(parent.Value, name, stream, true), RETRIES);

            return new FileInfoContract(item.Id, item.Name, item.DateLastSynced, FileSystemExtensions.Later(item.DateLastSynced, item.ModifiedTime), item.Size, null);
        }

        public async Task<bool> RemoveItemAsync(RootName root, FileSystemId target, bool recurse)
        {
            var context = await RequireContextAsync(root);

            var success = await AsyncFunc.RetryAsync<bool, ServerException>(async () => await context.Client.FileSystemManager.DeleteAsync(target.Value), RETRIES);

            return success;
        }

        public /*async*/ Task<FileSystemInfoContract> RenameItemAsync(RootName root, FileSystemId target, string newName, Func<FileSystemInfoLocator> locatorResolver)
        {
            //var context = await RequireContext(root);

            //var success = await AsyncFunc.Retry<bool, ServerException>(async () => await context.Client.FileSystemManager.RenameFileAsync(target.Value, newName, false), RETRIES);
            //if (!success)
            //    throw new ApplicationException(string.Format(CultureInfo.CurrentCulture, Resources.RenameFailed, target.Value, newName));

            //var renamedItemPath = target.Value.Substring(0, target.Value.LastIndexOf('/')) + @"/" + newName;
            //var item = await AsyncFunc.Retry<FileSystem, ServerException>(async () => await context.Client.FileSystemManager.GetFileSystemInformationAsync(renamedItemPath), RETRIES);

            //return item.ToFileSystemInfoContract();
            return Task.FromException<FileSystemInfoContract>(new NotSupportedException(Properties.Resources.RenamingOfFilesOrDirectoriesNotSupported));
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode", Justification = "Debugger Display")]
        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        private static string DebuggerDisplay() => nameof(CopyGateway);
    }
}
