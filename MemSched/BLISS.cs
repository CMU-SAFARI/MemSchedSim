using System;
using System.Collections.Generic;
using System.Text;

using System.IO;
using System.Linq;

namespace MemMap
{
    public class BLISS : MemSched
    {
        //shuffle
        int shuffle_cycles_left;
        int[] oldest_streak;

        int[] mark;
        int last_req_pid;
        int oldest_streak_global;

        //shuffle

        public BLISS()
        {

            shuffle_cycles_left = Config.sched.shuffle_cycles;
            mark = new int[Config.N];

        }

        public override void initialize()
        {
            oldest_streak = new int[meta_mctrl.get_bmax()];
        }


        public override void enqueue_req(Req req) { }
        public override void dequeue_req(Req req) { }

        public override Req better_req(Req req1, Req req2)
        {

            if (mark[req1.pid] != 1 ^ mark[req2.pid] != 1) {
               if (mark[req1.pid] != 1) 
               {
                   return req1;
               }
               else 
               {
                   return req2;
               }
            }


            bool hit1 = is_row_hit(req1);
            bool hit2 = is_row_hit(req2);

 
            if (hit1 ^ hit2) {
                if (hit1) return req1;
                else return req2;
            }

            if (req1.ts_arrival <= req2.ts_arrival) return req1;
            else return req2;
        }

        public override void tick()
        {
            base.tick();

            //shuffle
            if (shuffle_cycles_left > 0) {
                shuffle_cycles_left--;
            }
            else 
            {
                shuffle_cycles_left = Config.sched.shuffle_cycles;
                clear_marking();
            }

        }

        public void clear_marking()
        {
            for (int p = 0; p < Config.N; p ++)
            {
//                Console.Write(" Proc " + p + " Mark " + mark[p] + "\n");
                mark[p] = 0;
            }
        }

        public override void issue_req(Req req)
        {
            if (req == null) return;
            count_streaks(req);
            uint bid;
            if (Config.sched.channel_level)
            {
                if (req.pid == last_req_pid && oldest_streak_global < Config.sched.row_hit_cap) {
                    oldest_streak_global += 1;
                }
                else if (req.pid == last_req_pid && oldest_streak_global == Config.sched.row_hit_cap)
                {
                    mark[req.pid] = 1;
                    oldest_streak_global = 1;
                }
                else {
                    oldest_streak_global = 1;
                }
                last_req_pid = req.pid;
            }
            else
            {
                bid = meta_mctrl.get_bid(req);

                if (meta_mctrl.is_req_to_cur_proc(req) && oldest_streak[bid] < Config.sched.row_hit_cap) {
                    oldest_streak[bid] += 1;
                }
                else if (meta_mctrl.is_req_to_cur_proc(req) && oldest_streak[bid] == Config.sched.row_hit_cap)
                {
                    mark[req.pid] = 1;
                    oldest_streak[bid] = 1;
//                    Console.Write(" OLDEST: Marking processor " + req.pid + "\n");
                }
                else {
                    oldest_streak[bid] = 1;
                }
            }
        }
    }
}
