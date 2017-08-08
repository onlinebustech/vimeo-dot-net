﻿using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using VimeoDotNet.Constants;
using VimeoDotNet.Enums;
using VimeoDotNet.Exceptions;
using VimeoDotNet.Models;
using VimeoDotNet.Net;

namespace VimeoDotNet
{
    public partial class VimeoClient
    {
        /// <summary>
        /// Complete upload file asynchronously
        /// </summary>
        /// <param name="uploadRequest">UploadRequest</param>
        /// <returns></returns>
        public async Task CompleteFileUploadAsync(IUploadRequest uploadRequest)
        {
            ThrowIfUnauthorized();

            try
            {
                var request = GenerateCompleteUploadRequest(uploadRequest.Ticket);
                var response = await request.ExecuteRequestAsync();
                CheckStatusCodeError(uploadRequest, response, "Error marking file upload as complete.");

                if (response.Headers.Location != null)
                {
                    uploadRequest.ClipUri = response.Headers.Location.ToString();
                }
            }
            catch (Exception ex)
            {
                if (ex is VimeoApiException)
                {
                    throw;
                }
                throw new VimeoUploadException("Error marking file upload as complete.", uploadRequest, ex);
            }
        }

        /// <summary>
        /// Continue upload file asynchronously
        /// </summary>
        /// <param name="uploadRequest">UploadRequest</param>
        /// <returns>Verification upload response</returns>
        public async Task<VerifyUploadResponse> ContinueUploadFileAsync(IUploadRequest uploadRequest)
        {
            if (uploadRequest.AllBytesWritten)
            {
                // Already done, there's nothing to do.
                return new VerifyUploadResponse
                {
                    Status = UploadStatusEnum.InProgress,
                    BytesWritten = uploadRequest.BytesWritten
                };
            }

            try
            {
                var request = await GenerateFileStreamRequest(uploadRequest.File, uploadRequest.Ticket,
                    chunkSize: uploadRequest.ChunkSize, written: uploadRequest.BytesWritten);
                var response = await request.ExecuteRequestAsync();
                CheckStatusCodeError(uploadRequest, response, "Error uploading file chunk.", HttpStatusCode.BadRequest);

                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    // something went wrong, figure out where we need to start over
                    return await VerifyUploadFileAsync(uploadRequest);
                }

                // Success, update total written
                uploadRequest.BytesWritten += uploadRequest.ChunkSize;
                uploadRequest.BytesWritten = Math.Min(uploadRequest.BytesWritten, uploadRequest.FileLength);
                return new VerifyUploadResponse
                {
                    Status = UploadStatusEnum.InProgress,
                    BytesWritten = uploadRequest.BytesWritten
                };
            }
            catch (Exception ex)
            {
                if (ex is VimeoApiException)
                {
                    throw;
                }
                throw new VimeoUploadException("Error uploading file chunk", uploadRequest, ex);
            }
        }

        /// <summary>
        /// Create new upload ticket asynchronously
        /// </summary>
        /// <returns>Upload ticket</returns>
        public async Task<UploadTicket> GetUploadTicketAsync()
        {
            try
            {
                var request = GenerateUploadTicketRequest();
                var response = await request.ExecuteRequestAsync<UploadTicket>();
                UpdateRateLimit(response);
                CheckStatusCodeError(null, response, "Error generating upload ticket.");

                return response.Content;
            }
            catch (Exception ex)
            {
                if (ex is VimeoApiException)
                {
                    throw;
                }
                throw new VimeoUploadException("Error generating upload ticket.", null, ex);
            }
        }

        /// <summary>
        /// Create new upload ticket for replace video asynchronously
        /// </summary>
        /// <param name="videoId">VideoId</param>
        /// <returns>Upload ticket</returns>
        public async Task<UploadTicket> GetReplaceVideoUploadTicketAsync(long videoId)
        {
            try
            {
                var request = GenerateReplaceVideoUploadTicketRequest(videoId);
                var response = await request.ExecuteRequestAsync<UploadTicket>();
                UpdateRateLimit(response);
                CheckStatusCodeError(null, response, "Error generating upload ticket to replace video.");

                return response.Content;
            }
            catch (Exception ex)
            {
                if (ex is VimeoApiException)
                {
                    throw;
                }
                throw new VimeoUploadException("Error generating upload ticket to replace video.", null, ex);
            }
        }

        /// <summary>
        /// Start upload file asynchronously
        /// </summary>
        /// <param name="fileContent">FileContent</param>
        /// <param name="chunkSize">ChunkSize</param>
        /// <param name="replaceVideoId">ReplaceVideoId</param>
        /// <returns></returns>
        public async Task<IUploadRequest> StartUploadFileAsync(IBinaryContent fileContent,
            int chunkSize = DEFAULT_UPLOAD_CHUNK_SIZE,
            long? replaceVideoId = null)
        {
            if (!fileContent.Data.CanRead)
            {
                throw new ArgumentException("fileContent should be readable");
            }
            if (fileContent.Data.CanSeek && fileContent.Data.Position > 0)
            {
                fileContent.Data.Position = 0;
            }

            UploadTicket ticket = replaceVideoId.HasValue 
                ? await GetReplaceVideoUploadTicketAsync(replaceVideoId.Value)
                : await GetUploadTicketAsync();

            var uploadRequest = new UploadRequest
            {
                Ticket = ticket,
                File = fileContent,
                ChunkSize = chunkSize
            };

            VerifyUploadResponse uploadStatus = await ContinueUploadFileAsync(uploadRequest);
            
            return uploadRequest;
        }

        /// <summary>
        /// Upload file part asynchronously
        /// </summary>
        /// <param name="fileContent">FileContent</param>
        /// <param name="chunkSize">ChunkSize</param>
        /// <param name="replaceVideoId">ReplaceVideoId</param>
        /// <returns>Upload request</returns>
        public async Task<IUploadRequest> UploadEntireFileAsync(IBinaryContent fileContent,
            int chunkSize = DEFAULT_UPLOAD_CHUNK_SIZE,
            long? replaceVideoId = null)
        {
            IUploadRequest uploadRequest = await StartUploadFileAsync(fileContent, chunkSize, replaceVideoId);

            VerifyUploadResponse uploadStatus = null;
            while (!uploadRequest.IsVerifiedComplete)
            {
                uploadStatus = await ContinueUploadFileAsync(uploadRequest);                

                if (uploadRequest.AllBytesWritten)
                {
                    // We presumably wrote all the bytes in the file, so verify with Vimeo that it
                    // is completed
                    uploadStatus = await VerifyUploadFileAsync(uploadRequest);
                    if (uploadStatus.Status == UploadStatusEnum.Completed)
                    {
                        // If completed, mark file as complete
                        await CompleteFileUploadAsync(uploadRequest);
                        uploadRequest.IsVerifiedComplete = true;
                    }
                    else if (uploadStatus.BytesWritten == uploadRequest.FileLength)
                    {
                        // Supposedly all bytes are written, but Vimeo doesn't think so, so just
                        // bail out
                        throw new VimeoUploadException(
                            string.Format(
                                "Vimeo failed to mark file as completed, Bytes Written: {0:N0}, Expected: {1:N0}.",
                                uploadStatus.BytesWritten),
                            uploadRequest);
                    }
                }
            }

            return uploadRequest;
        }

        /// <summary>
        /// Verify upload file part asynchronously
        /// </summary>
        /// <param name="uploadRequest">UploadRequest</param>
        /// <returns>Verification reponse</returns>
        public async Task<VerifyUploadResponse> VerifyUploadFileAsync(IUploadRequest uploadRequest)
        {
            try
            {
                var request =
                    await GenerateFileStreamRequest(uploadRequest.File, uploadRequest.Ticket, verifyOnly: true);
                var response = await request.ExecuteRequestAsync();
                var verify = new VerifyUploadResponse();
                CheckStatusCodeError(uploadRequest, response, "Error verifying file upload.", (HttpStatusCode)308);

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    verify.BytesWritten = uploadRequest.FileLength;
                    verify.Status = UploadStatusEnum.Completed;
                }
                else if (response.StatusCode == (HttpStatusCode)308)
                {
                    verify.Status = UploadStatusEnum.InProgress;
                    if (!response.Headers.Contains("Range")) 
                        return verify;
                    var startIndex = 0;
                    var endIndex = 0;
                    var match = RangeRegex.Match(response.Headers.GetValues("Range").First());
                    if (match.Success
                        && int.TryParse(match.Groups["start"].Value, out startIndex)
                        && int.TryParse(match.Groups["end"].Value, out endIndex))
                    {
                        verify.BytesWritten = endIndex - startIndex;
                        if (verify.BytesWritten == uploadRequest.FileLength)
                        {
                            verify.Status = UploadStatusEnum.Completed;
                        }
                    }
                }
                else
                {
                    verify.Status = UploadStatusEnum.NotFound;
                }

                return verify;
            }
            catch (Exception ex)
            {
                if (ex is VimeoApiException)
                {
                    throw;
                }
                throw new VimeoUploadException("Error verifying file upload.", uploadRequest, ex);
            }
        }

        private IApiRequest GenerateCompleteUploadRequest(UploadTicket ticket)
        {
            IApiRequest request = ApiRequestFactory.GetApiRequest(AccessToken);
            request.Method = HttpMethod.Delete;
            request.Path = ticket.complete_uri;
            return request;
        }

        private async Task<IApiRequest> GenerateFileStreamRequest(IBinaryContent fileContent, UploadTicket ticket,
            long written = 0, int? chunkSize = null, bool verifyOnly = false)
        {
            if (ticket == null || string.IsNullOrWhiteSpace(ticket.ticket_id))
            {
                throw new ArgumentException("Invalid upload ticket.");
            }
            if (fileContent.Data.Length > ticket.user.upload_quota.space.free)
            {
                throw new InvalidOperationException(
                    "User does not have enough free space to upload this video. Remaining space: " +
                    ticket.quota.free_space + ".");
            }

            var request = ApiRequestFactory.GetApiRequest();
            request.Method = HttpMethod.Put;
            request.ExcludeAuthorizationHeader = true;
            request.Path = ticket.upload_link_secure;
            
            if (verifyOnly)
            {
                request.Body = new ByteArrayContent(new byte [0]);
            }
            else
            {
                if (chunkSize.HasValue)
                {
                    var startIndex = fileContent.Data.CanSeek ? fileContent.Data.Position : written;
                    var endIndex = Math.Min(startIndex + chunkSize.Value, fileContent.Data.Length);
                    request.Body = new ByteArrayContent(await fileContent.ReadAsync(startIndex, endIndex), (int)startIndex, (int)endIndex);
                }
                else
                {
                    request.Body = new ByteArrayContent(await fileContent.ReadAllAsync());
                }
            }

            return request;
        }

        private IApiRequest GenerateUploadTicketRequest()
        {
            ThrowIfUnauthorized();

            IApiRequest request = ApiRequestFactory.GetApiRequest(AccessToken);
            request.Method = HttpMethod.Post;
            request.Path = Endpoints.UploadTicket;
            request.Query.Add("type", "streaming");
            return request;
        }

        private IApiRequest GenerateReplaceVideoUploadTicketRequest(long clipId)
        {
            ThrowIfUnauthorized();

            var request = ApiRequestFactory.GetApiRequest(AccessToken);
            request.Method = HttpMethod.Put;
            request.Path = Endpoints.VideoReplaceFile;
            request.UrlSegments.Add("clipId", clipId.ToString());
            request.Query.Add("type", "streaming");
            return request;
        }
    }
}