using System;
using System.Timers;
using APSIM.POStats.Shared;
using APSIM.POStats.Shared.Models;
using APSIM.POStats.Shared.GitHub;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using APSIM.POStats.Portal.Models;

namespace APSIM.POStats.Portal.Controllers
{
    [Route("api")]
    [ApiController]
    public class Api : ControllerBase
    {
        /// <summary>The database context.</summary>
        private readonly StatsDbContext statsDb;

        /// <summary>Lock for adding to the db</summary>
        private static object _lock = new();

        /// <summary>Final timeout in minutes. Controls how long to wait before finally closing a pull request.</summary>
        private const double FINAL_TIMEOUT = 50.0;

        /// <summary>Event handler for finish timer.</summary>
        private static void OnCheckIfFinished(Object source, ElapsedEventArgs e)
        {
            string url = Environment.GetEnvironmentVariable("POSTATS_UPLOAD_URL");
            if (string.IsNullOrEmpty(url))
                throw new Exception($"Cannot find environment variable POSTATS_UPLOAD_URL");

            PullRequestTimer timer = source as PullRequestTimer;

            try
            {
                Task<string> response = WebUtilities.GetAsync($"{url}api/count?pullrequestnumber={timer.PullRequest}&commitid={timer.Commit}");
                response.Wait();
                
                string result = response.Result.ToString();
                if (!string.IsNullOrEmpty(result))
                {
                    int count = int.Parse(result);

                    double minutes = (DateTime.Now - timer.StartTime).TotalMinutes;

                    if (minutes >= FINAL_TIMEOUT || count >= timer.CountTotal)
                    {
                        timer.Stop();
                        response = WebUtilities.GetAsync($"{url}api/close?pullRequestNumber={timer.PullRequest}&commitId={timer.Commit}");
                        response.Wait();

                        if (count < timer.CountTotal)
                            GitHub.SetStatus(timer.PullRequest, timer.Commit, VariableComparison.Status.Missing, "Timeout Error");
                    }
                }
            }
            catch
            {
                timer.Stop();
            }
        }

        /// <summary>Constructor.</summary>
        /// <param name="stats">The database context.</param>
        public Api(StatsDbContext stats)
        {
            statsDb = stats;
        }

        /// <summary>Invoked by collector to open a pull request.</summary>
        /// <param name="pullrequestnumber">The number of the pull request to open.</param>
        /// <param name="commitid">The commit id of the pull request.</param>
        /// <param name="count">The number of expected validation tasks to be run.</param>
        /// <param name="author">The author of the pull request.</param>
        /// <param name="pool">The name of the Azure Batch pool to be used for this pull request.</param>
        /// <returns></returns>
        [HttpGet("open")]
        public IActionResult Open(int pullrequestnumber, string commitid, int count, string author, string pool)
        {
            Console.WriteLine($"\"{author}\" opening PR \"{pullrequestnumber}\" with commit \"{commitid}\" and count \"{count}\"");
            if (pullrequestnumber == 0)
                return BadRequest("You must supply a pullrequestnumber");
            if (string.IsNullOrEmpty(commitid))
                return BadRequest("You must supply a commitid");
            if (string.IsNullOrEmpty(author))
                return BadRequest("You must supply an author");
            if (string.IsNullOrEmpty(pool))
                return BadRequest("You must supply a pool name");
            try
            {
                statsDb.OpenPullRequest(pullrequestnumber, commitid, author, count, pool);

                GitHub.SetStatus(pullrequestnumber, commitid, VariableComparison.Status.Running, $"Running {count} Validation Tasks");

                // Create a timer to check how many files have been returned
                PullRequestTimer finishTimer = new PullRequestTimer { Interval = 60000, PullRequest = pullrequestnumber, Commit = commitid, CountTotal = count };
                finishTimer.Elapsed += OnCheckIfFinished;
                finishTimer.AutoReset = true;
                finishTimer.StartTime = DateTime.Now;
                finishTimer.Start();
            }
            catch (Exception ex)
            {
                return BadRequest(ex.ToString());
            }

            return Ok();
        }

        /// <summary>Invoked by collector to close a pull request.</summary>
        /// <param name="pullrequestnumber">The number of the pull request to close.</param>
        /// <returns></returns>
        [HttpGet("close")]
        public async Task<IActionResult> Close(int pullrequestnumber, string commitid)
        {
            Console.WriteLine($"api/close called");
            
            if (pullrequestnumber == 0)
                return BadRequest("You must supply a pull request number");

            if (commitid.Length == 0)
                return BadRequest("You must supply a commit number");

            if (statsDb.PullRequestWithCommitExists(pullrequestnumber, commitid))
            {
                Console.WriteLine($"Closing PR \"{pullrequestnumber} with commit {commitid}\"");
                PullRequestDetails pullRequest = new();
                try
                {
                    // Send pass/fail to gitHub
                    pullRequest = statsDb.ClosePullRequest(pullrequestnumber);

                    if (PullRequestFunctions.HasExceptionInLogs(pullRequest))
                    {
                        GitHub.SetStatus(pullrequestnumber, commitid, VariableComparison.Status.Missing, "Exception Thrown");
                    }
                    else
                    {
                        VariableComparison.Status status = PullRequestFunctions.GetStatus(pullRequest);
                        GitHub.SetStatus(pullrequestnumber, commitid, status);
                    }

                    if (string.IsNullOrEmpty(pullRequest.Pool))
                        throw new Exception("No pool associated with this pull request. Pool is required to close the Azure Batch pool.");
                        
                }
                catch (Exception ex)
                {
                    return BadRequest(ex.ToString());
                }

                // Close the Azure Batch pool
                try
                {                    
                    await AzureBatchManager.CloseBatchPoolAsync(pullRequest.Pool);
                }
                catch(InvalidOperationException ex)
                {
                    // If we get an exception here, it likely means the pool was
                    // already closed. 
                    // Log the error, but do not return an error to the user as 
                    // the PR was successfully closed in the database and GitHub.
                    return Ok($"PR closed, but failed to close Azure Batch pool ({pullRequest.Pool}). Error: {ex}");
                }
            }
            else
            {
                PullRequestDetails pr = statsDb.GetPullRequest(pullrequestnumber);
                if (pr == null)
                    return BadRequest($"A PR with {pullrequestnumber} does not exist in the database");
                else
                    return BadRequest($"A PR with {pullrequestnumber} does exist, but has commit number {pr.Commit}, and you submitted a commit of {commitid}");
            }

            return Ok();
        }

        /// <summary>Returns the number of files remaining for a pull request</summary>
        /// <param name="pullrequestnumber">The number of the pull request.</param>
        /// <param name="pullrequestnumber">The commit id of the pull request.</param>
        /// <returns></returns>
        [HttpGet("count")]
        public IActionResult Count(int pullrequestnumber, string commitid)
        {
            //Console.WriteLine($"api/count");
            
            if (pullrequestnumber == 0)
                return BadRequest("You must supply a pull request number");

            if (commitid.Length == 0)
                return BadRequest("You must supply a commit number");

            if (statsDb.PullRequestWithCommitExists(pullrequestnumber, commitid))
            {
                try
                {
                    var count = statsDb.GetNumberOfCompletesInPullRequest(pullrequestnumber, commitid);
                    return Ok(count);
                }
                catch (Exception ex)
                {
                    return BadRequest(ex.ToString());
                }
            }
            else
            {
                PullRequestDetails pr = statsDb.GetPullRequest(pullrequestnumber);
                if (pr == null)
                    return BadRequest($"A PR with {pullrequestnumber} does not exist in the database");
                else
                    return BadRequest($"A PR with {pullrequestnumber} does exist, but has commit number {pr.Commit}, and you submitted a commit of {commitid}");
            }
        }

        /// <summary>Invoked by collector to upload a pull request.</summary>
        /// <param name="pullRequest"></param>
        /// <returns></returns>
        [HttpPost("adddata")]
        [RequestSizeLimit(100_000_000)]
        public IActionResult PostAsync([FromBody]PullRequestDetails pullrequest)
        {
            if (pullrequest == null)
                return BadRequest("You must supply a pull request");

            lock (_lock)
            {
                try
                {
                    PullRequestDetails pr = statsDb.AddDataToPullRequest(pullrequest);
                }
                catch (Exception ex)
                {
                    return BadRequest(ex.ToString());
                }
            }
            return Ok();
        }
    }
}